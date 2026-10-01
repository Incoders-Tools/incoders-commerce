using System.Text.Json;
using Commerce.Application.Audit;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Audit;
using Npgsql;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// The cloud's <see cref="IAuditSink"/> (persist-web-orders): the access decisions of a customer
/// ordering credential are appended to `audit_log` instead of an in-memory queue that was lost on every
/// restart. Each write is its own transaction under the entry's organization scope (the table's RLS
/// insert policy requires it) through <see cref="AuditLogWriter"/>. It is fail-closed on purpose: a
/// decision that cannot be audited surfaces as an exception, never as a silent gap.
///
/// Mapping: actor kind <see cref="AuditActorKinds.Customer"/>, actor = the customer when the credential
/// resolved to one (the nil id for an unknown credential), entity = the access decision itself (its
/// correlation id), action = <c>{entry.Action}.{entry.Outcome}</c>, and the reason, branch, time and
/// correlation id as the JSON new value.
/// </summary>
public sealed class PostgresAuditSink : IAuditSink
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuditSink(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Synchronous bridge for the interface; async callers use <see cref="RecordAsync"/>.</summary>
    public void Record(AuditEntry entry) => RecordAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(entry.OrganizationId), ct);

        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            ActorKind: AuditActorKinds.Customer,
            ActorId: entry.ActorId,
            OrganizationId: entry.OrganizationId,
            EntityType: entry.Action,
            EntityId: entry.CorrelationId,
            Action: $"{entry.Action}.{entry.Outcome}",
            OldValueJson: null,
            NewValueJson: JsonSerializer.Serialize(new
            {
                reason = entry.Reason,
                outcome = entry.Outcome,
                branchId = entry.BranchId,
                correlationId = entry.CorrelationId,
                occurredAtUtc = entry.OccurredAtUtc,
            })), ct);

        await tx.CommitAsync(ct);
    }
}
