using Commerce.Application.Payments;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Payments;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed <see cref="IPaymentLedgerStore"/> implementation
/// (commerce-payments design.md "Interfaces / Contracts"), mirroring
/// <see cref="PostgresCloudInboxStore"/>'s exact shape: raw
/// <see cref="NpgsqlDataSource"/>, `set_config` always the FIRST statement in
/// its own transaction. Append-only — no UPDATE, no DELETE is ever issued
/// here, matching the `app_runtime` grant (0011).
///
/// <see cref="IPaymentLedgerStore"/> is an Application-layer port with no
/// dependency on <see cref="CloudTenantScope"/> (Application never depends on
/// Cloud.Api); this store derives the tenant scope from
/// <see cref="PaymentEntry.OrganizationId"/> / the caller-supplied
/// <c>organizationId</c> parameter instead.
/// </summary>
public sealed class PostgresPaymentStore : IPaymentLedgerStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPaymentStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, CancellationToken ct)
    {
        await using var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx);
        scopeCmd.Parameters.AddWithValue(organizationId.ToString());
        await scopeCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Append-only insert. The <c>operation_id</c> column reuses
    /// <see cref="PaymentEntry.EntryId"/> as the idempotency key (ADR-003 —
    /// the same "stable business identity IS the sync operation id" pattern
    /// <c>Order.OrderId</c> already uses). A duplicate append (same EntryId)
    /// is a no-op — <c>ON CONFLICT (operation_id) DO NOTHING</c> — so a retried
    /// request never produces a second row.
    /// </summary>
    public async Task AppendAsync(PaymentEntry entry, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, entry.OrganizationId, ct);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO payment_entries
                (entry_id, organization_id, operation_id, subject_kind, subject_id,
                 entry_kind, method, amount, approval_state, reverses_entry_id,
                 provider_reference, actor_id, recorded_at_utc)
            VALUES ($1, $2, $1, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (operation_id) DO NOTHING
            """, connection, tx);
        cmd.Parameters.AddWithValue(entry.EntryId);
        cmd.Parameters.AddWithValue(entry.OrganizationId);
        cmd.Parameters.AddWithValue(entry.Subject.Kind.ToString());
        cmd.Parameters.AddWithValue(entry.Subject.SubjectId);
        cmd.Parameters.AddWithValue(entry.Kind.ToString());
        cmd.Parameters.AddWithValue(entry.Method.ToString());
        cmd.Parameters.AddWithValue(entry.Amount);
        cmd.Parameters.AddWithValue(entry.ApprovalState.ToString());
        cmd.Parameters.AddWithValue((object?)entry.ReversesEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.ProviderReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.ActorId);
        cmd.Parameters.AddWithValue(entry.RecordedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PaymentEntry>> GetEntriesAsync(PaymentSubject subject, Guid organizationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, organizationId, ct);

        var results = new List<PaymentEntry>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT entry_id, organization_id, subject_kind, subject_id, entry_kind, method,
                   amount, approval_state, reverses_entry_id, provider_reference, actor_id, recorded_at_utc
            FROM payment_entries
            WHERE subject_kind = $1 AND subject_id = $2
            ORDER BY recorded_at_utc
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(subject.Kind.ToString());
            cmd.Parameters.AddWithValue(subject.SubjectId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    private static PaymentEntry Read(NpgsqlDataReader reader) => new(
        EntryId: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        Subject: new PaymentSubject(Enum.Parse<PaymentSubjectKind>(reader.GetString(2)), reader.GetGuid(3)),
        Kind: Enum.Parse<PaymentEntryKind>(reader.GetString(4)),
        Method: Enum.Parse<PaymentMethod>(reader.GetString(5)),
        Amount: reader.GetDecimal(6),
        ApprovalState: Enum.Parse<PaymentApprovalState>(reader.GetString(7)),
        ReversesEntryId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
        ProviderReference: reader.IsDBNull(9) ? null : reader.GetString(9),
        ActorId: reader.GetGuid(10),
        RecordedAtUtc: reader.GetFieldValue<DateTimeOffset>(11));
}
