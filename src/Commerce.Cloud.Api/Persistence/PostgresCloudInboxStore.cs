using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed <see cref="ICloudInboxStore"/> implementation
/// (design.md "Persistence access" / "Pooling + tenant scope"). Raw
/// <see cref="NpgsqlDataSource"/> + parameterized commands — no EF Core.
///
/// Tenant scoping is ALWAYS the first statement inside the same transaction
/// as the work it protects: `SET` cannot be parameterized, so every operation
/// opens an explicit <see cref="NpgsqlTransaction"/> and issues
/// `SELECT set_config('app.current_org_id', $1, true)` (transaction-scoped,
/// `is_local: true`) before the tenant-scoped query, then commits. This is
/// exactly the shape design.md's "Interfaces / Contracts" sample specifies,
/// and is the shape that keeps transaction-mode pooling (Supavisor / PgBouncer
/// port 6543) safe: `SET LOCAL`/`set_config(..., true)` cannot outlive the
/// transaction, so it can never leak to whichever client borrows the pooled
/// server connection next.
///
/// Schema mirrors `deploy/dev/db/init-rls.sql` / `deploy/db/migrations/0001_init_rls.sql`:
/// the `sync_inbox` table, `FORCE ROW LEVEL SECURITY`, and the
/// `sync_inbox_tenant_isolation` policy filtering on
/// `current_setting('app.current_org_id', true)::uuid`. This class connects
/// as the non-owner `app_runtime` role (never the table owner / service_role)
/// so RLS is enforced even if a bug here ever forgot the scope statement.
/// </summary>
public sealed class PostgresCloudInboxStore : ICloudInboxStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCloudInboxStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public InboundApplyResult TryApplyInbound(CloudTenantScope scope, SyncEnvelope envelope)
    {
        if (scope.OrganizationId != envelope.OrganizationId)
        {
            // Defense in depth: reject an envelope claiming another
            // organization before it ever reaches the database, mirroring
            // CloudInboxStore's in-memory equivalent.
            return new InboundApplyResult(InboundApplyOutcome.Denied, envelope.OperationId);
        }

        return TryApplyInboundAsync(scope, envelope, CancellationToken.None).GetAwaiter().GetResult();
    }

    public bool Acknowledge(CloudTenantScope scope, Guid operationId) =>
        AcknowledgeAsync(scope, operationId, CancellationToken.None).GetAwaiter().GetResult();

    public SyncOperationStatus? GetStatus(CloudTenantScope scope, Guid operationId) =>
        GetStatusAsync(scope, operationId, CancellationToken.None).GetAwaiter().GetResult();

    public IReadOnlyList<SyncEnvelope> GetInboxFor(CloudTenantScope scope) =>
        GetInboxForAsync(scope, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<InboundApplyResult> TryApplyInboundAsync(CloudTenantScope scope, SyncEnvelope envelope, CancellationToken ct)
    {
        if (scope.OrganizationId != envelope.OrganizationId)
        {
            return new InboundApplyResult(InboundApplyOutcome.Denied, envelope.OperationId);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using (var existsCmd = new NpgsqlCommand(
            "SELECT 1 FROM sync_inbox WHERE operation_id = $1", connection, tx))
        {
            existsCmd.Parameters.AddWithValue(envelope.OperationId);
            var existing = await existsCmd.ExecuteScalarAsync(ct);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new InboundApplyResult(InboundApplyOutcome.DuplicateIgnored, envelope.OperationId);
            }
        }

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO sync_inbox
                (operation_id, organization_id, branch_id, aggregate_id, aggregate_version,
                 actor_id, correlation_id, occurred_at_utc, payload_kind, payload, status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, 'Pending')
            """, connection, tx))
        {
            insertCmd.Parameters.AddWithValue(envelope.OperationId);
            insertCmd.Parameters.AddWithValue(envelope.OrganizationId);
            insertCmd.Parameters.AddWithValue(envelope.BranchId);
            insertCmd.Parameters.AddWithValue(envelope.AggregateId);
            insertCmd.Parameters.AddWithValue(envelope.AggregateVersion);
            insertCmd.Parameters.AddWithValue(envelope.ActorId);
            insertCmd.Parameters.AddWithValue(envelope.CorrelationId);
            insertCmd.Parameters.AddWithValue(envelope.OccurredAtUtc);
            insertCmd.Parameters.AddWithValue(envelope.PayloadKind);
            insertCmd.Parameters.AddWithValue(envelope.Payload);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new InboundApplyResult(InboundApplyOutcome.Applied, envelope.OperationId);
    }

    public async Task<bool> AcknowledgeAsync(CloudTenantScope scope, Guid operationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var updateCmd = new NpgsqlCommand(
            "UPDATE sync_inbox SET status = 'Acknowledged', acknowledged_at_utc = now() WHERE operation_id = $1",
            connection, tx);
        updateCmd.Parameters.AddWithValue(operationId);
        var rows = await updateCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);

        // RLS default-deny means a cross-org operation id simply matches zero
        // rows here — never a distinguishable "found but denied" outcome.
        return rows > 0;
    }

    public async Task<SyncOperationStatus?> GetStatusAsync(CloudTenantScope scope, Guid operationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var selectCmd = new NpgsqlCommand(
            "SELECT status FROM sync_inbox WHERE operation_id = $1", connection, tx);
        selectCmd.Parameters.AddWithValue(operationId);
        var status = await selectCmd.ExecuteScalarAsync(ct);

        await tx.CommitAsync(ct);

        return status switch
        {
            null => null,
            string s => Enum.Parse<SyncOperationStatus>(s),
            _ => null
        };
    }

    public async Task<IReadOnlyList<SyncEnvelope>> GetInboxForAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<SyncEnvelope>();

        await using (var selectCmd = new NpgsqlCommand(
            """
            SELECT operation_id, organization_id, branch_id, aggregate_id, aggregate_version,
                   actor_id, correlation_id, occurred_at_utc, payload_kind, payload
            FROM sync_inbox
            """, connection, tx))
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(new SyncEnvelope(
                    OperationId: reader.GetGuid(0),
                    ContractVersion: 1,
                    OrganizationId: reader.GetGuid(1),
                    BranchId: reader.GetGuid(2),
                    AggregateId: reader.GetGuid(3),
                    AggregateVersion: reader.GetInt64(4),
                    ActorId: reader.GetGuid(5),
                    CorrelationId: reader.GetGuid(6),
                    OccurredAtUtc: reader.GetFieldValue<DateTimeOffset>(7),
                    PayloadKind: reader.GetString(8),
                    Payload: reader.GetString(9)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Tenant scoping is ALWAYS the first statement inside the transaction.
    /// `SET` cannot be parameterized, so `set_config` is used as an ordinary
    /// parameterized statement with `is_local: true` — transaction-scoped,
    /// never leaking to another client on a shared pooled connection.
    /// </summary>
    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await using var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx);
        scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
        await scopeCmd.ExecuteNonQueryAsync(ct);
    }
}
