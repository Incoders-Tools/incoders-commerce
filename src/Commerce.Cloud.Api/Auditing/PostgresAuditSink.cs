using System.Text.Json;
using Commerce.Application.Audit;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// The cloud's <see cref="IAuditSink"/> (persist-web-orders): the access decisions of a customer
/// ordering credential are appended to `audit_log` instead of an in-memory queue that was lost on every
/// restart. Each write is its own transaction under the entry's organization scope (the table's RLS
/// insert policy requires it) through <see cref="AuditLogWriter"/>.
///
/// FAIL OPEN with alerting (owner decision 2026-10-01): if the row cannot be written the customer's
/// access decision proceeds unchanged and the failure is logged at Error level (organization, decision,
/// correlation id, exception) so it can be investigated; catalog access is never blocked by the audit
/// write. Only cancellation still propagates (the caller is gone).
///
/// Registered ONLY for the customer catalog access path (<see cref="CustomerCatalogAccessAuditRegistration"/>);
/// it hardcodes actor kind `customer`, so it must never be the shared <see cref="IAuditSink"/>.
///
/// Mapping: actor kind <see cref="AuditActorKinds.Customer"/>, actor = the customer when the credential
/// resolved to one (the nil id for an unknown credential), entity = the access decision itself (its
/// correlation id), action = <c>{entry.Action}.{entry.Outcome}</c>, and the reason, branch, time and
/// correlation id as the JSON new value.
/// </summary>
public sealed class PostgresAuditSink : IAuditSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresAuditSink> _logger;

    public PostgresAuditSink(NpgsqlDataSource dataSource, ILogger<PostgresAuditSink>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger ?? NullLogger<PostgresAuditSink>.Instance;
    }

    /// <summary>
    /// Not supported on purpose: a blocking bridge over the database call would park a request thread.
    /// The only consumer (<c>CustomerCatalogAccessService</c>) is async end to end and uses <see cref="RecordAsync"/>.
    /// </summary>
    public void Record(AuditEntry entry) =>
        throw new NotSupportedException("PostgresAuditSink is asynchronous only; use RecordAsync.");

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        try
        {
            await WriteAsync(entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Customer access audit write failed; the decision proceeds (fail-open). Organization {OrganizationId}, decision {Action}.{Outcome}, reason {Reason}, correlation {CorrelationId}",
                entry.OrganizationId, entry.Action, entry.Outcome, entry.Reason, entry.CorrelationId);
        }
    }

    private async Task WriteAsync(AuditEntry entry, CancellationToken ct)
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
