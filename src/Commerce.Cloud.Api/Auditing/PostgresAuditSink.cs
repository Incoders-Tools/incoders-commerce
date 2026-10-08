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
/// The cloud's <see cref="IAuditSink"/>: the shared sink of every consumer (staff authorization and catalog
/// management as `org-user`, customer catalog access as `customer`) appends to `audit_log` instead of an
/// in-memory queue that was lost on every restart. Each write is its own transaction under the entry's
/// organization scope (the table's RLS insert policy requires it) through <see cref="AuditLogWriter"/>.
/// Every <see cref="AuditEntry"/> carries an organization, so no entry is skipped for lack of one.
///
/// FAIL OPEN with alerting (owner decision 2026-10-01): if the row cannot be written the decision proceeds
/// unchanged and the failure is logged at Error level (organization, decision, correlation id, exception)
/// so it can be investigated; the audited action is never blocked by the audit write. Only cancellation
/// of the caller's own token still propagates (the caller is gone); any other cancellation, such as a
/// timeout, fails open like every other failure.
///
/// Mapping: actor kind from <see cref="AuditEntry.ActorKind"/>, actor = <see cref="AuditEntry.ActorId"/>
/// (the nil id for an unknown customer credential), entity = the decision itself (its correlation id),
/// action = <c>{entry.Action}.{entry.Outcome}</c>, and the reason, branch, time and correlation id as the
/// JSON new value.
///
/// The synchronous <see cref="Record"/> performs the same durable, fail-open write and blocks the calling
/// thread while it runs. Async callers (the cloud endpoints) use <see cref="RecordAsync"/> through
/// <c>TenantAuthorizationService.AuthorizeAsync</c>; the blocking form exists only because the shared
/// contract is also implemented synchronously by the local branch and updater paths.
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

    public void Record(AuditEntry entry) =>
        RecordAsync(entry, CancellationToken.None).GetAwaiter().GetResult();

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        try
        {
            await WriteAsync(entry, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogError(
                ex,
                "Audit write failed; the decision proceeds (fail-open). Organization {OrganizationId}, decision {Action}.{Outcome}, reason {Reason}, correlation {CorrelationId}",
                entry.OrganizationId, entry.Action, entry.Outcome, entry.Reason, entry.CorrelationId);
        }
    }

    private static string ActorKindOf(AuditActorKind kind) => kind switch
    {
        AuditActorKind.OrgUser => AuditActorKinds.OrgUser,
        AuditActorKind.Device => AuditActorKinds.Device,
        AuditActorKind.Customer => AuditActorKinds.Customer,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown audit actor kind."),
    };

    private async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(entry.OrganizationId), ct);

        await AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            ActorKind: ActorKindOf(entry.ActorKind),
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
