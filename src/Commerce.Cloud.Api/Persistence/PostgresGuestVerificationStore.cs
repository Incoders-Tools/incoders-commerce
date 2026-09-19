using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed guest-verification store (commerce-guest-ordering
/// design.md "Interfaces / Contracts"), following
/// <see cref="PostgresPasswordRecoveryStore"/>'s exact shape: injected
/// <see cref="NpgsqlDataSource"/>, scoped methods open their own
/// <see cref="NpgsqlTransaction"/> with `set_config('app.current_org_id', ...)`
/// as the first statement — except <see cref="FindAsync"/>, which is
/// deliberately UNSCOPED because confirm has no org until the row is read.
/// </summary>
public sealed class PostgresGuestVerificationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresGuestVerificationStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Issues a new verification row and, in the SAME transaction, supersedes
    /// (marks consumed) every OTHER prior unconsumed row for the same
    /// organization + contact address (design.md "same tx: supersede prior
    /// unconsumed rows for the same contact") so an earlier request cannot be
    /// confirmed after a newer one is issued.
    /// </summary>
    public async Task<Guid> IssueAsync(
        CloudTenantScope scope, string documentId, GuestContactChannel channel, string contactAddress,
        string codeHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var id = Guid.NewGuid();

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO guest_order_verifications
                (id, organization_id, document_id, contact_channel, contact_address, code_hash, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, tx))
        {
            insertCmd.Parameters.AddWithValue(id);
            insertCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertCmd.Parameters.AddWithValue(documentId);
            insertCmd.Parameters.AddWithValue(channel.ToString());
            insertCmd.Parameters.AddWithValue(contactAddress);
            insertCmd.Parameters.AddWithValue(codeHash);
            insertCmd.Parameters.AddWithValue(expiresAt);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var supersedeCmd = new NpgsqlCommand(
            """
            UPDATE guest_order_verifications SET consumed_at = now()
            WHERE organization_id = $1 AND contact_address = $2 AND consumed_at IS NULL AND id != $3
            """, connection, tx))
        {
            supersedeCmd.Parameters.AddWithValue(scope.OrganizationId);
            supersedeCmd.Parameters.AddWithValue(contactAddress);
            supersedeCmd.Parameters.AddWithValue(id);
            await supersedeCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Deliberately UNSCOPED (no `set_config` at all): confirm resolves the
    /// row via `guest_order_verifications_lookup`'s `USING (true)` policy
    /// BEFORE any tenant scope is known.
    /// </summary>
    public async Task<GuestVerificationRecord?> FindAsync(Guid verificationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, organization_id, document_id, contact_channel, contact_address, code_hash,
                   attempt_count, requested_at, expires_at, confirmed_at, consumed_at, consumed_order_id
            FROM guest_order_verifications WHERE id = $1
            """, connection, tx);
        cmd.Parameters.AddWithValue(verificationId);

        GuestVerificationRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = new GuestVerificationRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    Enum.Parse<GuestContactChannel>(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                    reader.IsDBNull(11) ? null : reader.GetGuid(11));
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// Increments `attempt_count` by 1. The table's own
    /// `CHECK (attempt_count &lt;= 5)` makes a 6th increment structurally
    /// impossible — the service layer never lets this be called once
    /// <see cref="GuestVerificationRecord.AttemptCount"/> already reads 5.
    /// </summary>
    public async Task RecordAttemptAsync(CloudTenantScope scope, Guid verificationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            "UPDATE guest_order_verifications SET attempt_count = attempt_count + 1 WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(verificationId);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Sets `confirmed_at`, turning the row into a single-use ticket
    /// (design.md "Verification state shape").
    /// </summary>
    public async Task ConfirmAsync(CloudTenantScope scope, Guid verificationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            "UPDATE guest_order_verifications SET confirmed_at = now() WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(verificationId);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Sets `consumed_at`/`consumed_order_id` — the ticket is spent
    /// immediately before <c>CloudOrderStore.Submit</c>, so one confirmation
    /// admits exactly one order (design.md "Verification state shape").
    /// </summary>
    public async Task ConsumeAsync(CloudTenantScope scope, Guid verificationId, Guid orderId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            "UPDATE guest_order_verifications SET consumed_at = now(), consumed_order_id = $1 WHERE id = $2",
            connection, tx);
        cmd.Parameters.AddWithValue(orderId);
        cmd.Parameters.AddWithValue(verificationId);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Tenant scoping is ALWAYS the first statement inside the transaction,
    /// mirroring <see cref="PostgresPasswordRecoveryStore"/> exactly.
    /// </summary>
    private static async Task SetTenantScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await using var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx);
        scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
        await scopeCmd.ExecuteNonQueryAsync(ct);
    }
}
