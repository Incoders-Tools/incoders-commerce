using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;

namespace Commerce.BranchNode;

/// <summary>
/// Orchestrates branch-owned sale commits, retry/ACK, and conflict review.
/// Reuses <see cref="TenantAuthorizationService"/> for conflict-review
/// authorization and <see cref="IAuditSink"/> for the resulting audit trail
/// (Component Reuse Policy — do not reimplement authorization/audit here).
/// </summary>
public sealed class BranchNodeService
{
    private static readonly ActionDefinition ReviewSyncConflict = new(
        Name: "review-sync-conflict",
        IsSensitive: true,
        RequiredPermission: Permission.ManageBranchSettings);

    private readonly BranchSyncStore _store;
    private readonly TenantAuthorizationService _authorizationService;
    private readonly IAuditSink _auditSink;
    private readonly Func<DateTimeOffset> _clock;

    public BranchNodeService(
        BranchSyncStore store,
        TenantAuthorizationService authorizationService,
        IAuditSink auditSink,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _authorizationService = authorizationService;
        _auditSink = auditSink;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public BranchOutboxCommitResult CompleteOfflineSale(
        Guid organizationId,
        Guid branchId,
        Guid actorId,
        Guid saleId,
        decimal totalAmount,
        Guid operationId,
        Guid correlationId)
    {
        var envelope = new SyncEnvelope(
            OperationId: operationId,
            ContractVersion: 1,
            OrganizationId: organizationId,
            BranchId: branchId,
            AggregateId: saleId,
            AggregateVersion: 1,
            ActorId: actorId,
            CorrelationId: correlationId,
            OccurredAtUtc: _clock(),
            PayloadKind: "sale",
            Payload: "{}");
        var effect = new SaleEffect(saleId, branchId, totalAmount, _clock());

        return _store.CommitSaleAtomically(envelope, effect);
    }

    public bool Acknowledge(Guid operationId) => _store.Acknowledge(operationId);

    public SyncStatusSnapshot GetStatus(Guid branchId, bool isOffline) => _store.GetStatus(branchId, isOffline);

    /// <summary>
    /// Two non-commuting envelope edits for the same aggregate. Both are kept
    /// as-is; nothing is written or merged until an authorized reviewer
    /// resolves them (ADR-002: last-write-wins is rejected).
    /// </summary>
    public MasterEditConflict DetectConflict(SyncEnvelope local, SyncEnvelope remote) =>
        new(Guid.NewGuid(), local.AggregateId, local, remote, _clock());

    public bool ReviewConflict(MasterEditConflict conflict, UserAccount reviewer, string decision, Guid correlationId)
    {
        var result = _authorizationService.Authorize(
            reviewer,
            new AccessRequest(reviewer.OrganizationId, conflict.Local.BranchId, ReviewSyncConflict, IsOffline: false, correlationId));

        if (!result.Allowed)
        {
            return false;
        }

        conflict.Resolve(new ConflictResolution(reviewer.Id, decision, _clock()));
        return true;
    }
}
