using Commerce.Application.Audit;
using Commerce.Domain.Audit;
using Commerce.Domain.Identity;

namespace Commerce.Application.Access;

/// <summary>
/// Enforces organization/branch scope, role permissions, revocation, and the
/// offline admin-snapshot freshness policy from ADR-002. Reusable by any
/// channel (local WPF, web) so authorization behaves identically everywhere.
/// </summary>
public sealed class TenantAuthorizationService
{
    private readonly IAuditSink _auditSink;
    private readonly TimeSpan _offlineFreshnessWindow;
    private readonly Func<DateTimeOffset> _clock;

    public TenantAuthorizationService(
        IAuditSink auditSink,
        TimeSpan? offlineFreshnessWindow = null,
        Func<DateTimeOffset>? clock = null)
    {
        _auditSink = auditSink;
        _offlineFreshnessWindow = offlineFreshnessWindow ?? TimeSpan.FromHours(24);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public AccessResult Authorize(UserAccount actor, AccessRequest request)
    {
        var result = Evaluate(actor, request);

        if (request.Action.IsSensitive)
        {
            _auditSink.Record(AuditOf(actor, request, result));
        }

        return result;
    }

    /// <summary>
    /// Same decision as <see cref="Authorize"/> with an awaited audit write, for async callers (the cloud)
    /// whose sink is durable: the request thread is not blocked on the database.
    /// </summary>
    public async Task<AccessResult> AuthorizeAsync(UserAccount actor, AccessRequest request, CancellationToken ct)
    {
        var result = Evaluate(actor, request);

        if (request.Action.IsSensitive)
        {
            await _auditSink.RecordAsync(AuditOf(actor, request, result), ct);
        }

        return result;
    }

    private AuditEntry AuditOf(UserAccount actor, AccessRequest request, AccessResult result) =>
        new(
            ActorId: actor.Id,
            ActorKind: AuditActorKind.OrgUser,
            OrganizationId: actor.OrganizationId,
            BranchId: request.TargetBranchId,
            Action: request.Action.Name,
            Outcome: result.Allowed ? "allowed" : "denied",
            OccurredAtUtc: _clock(),
            CorrelationId: request.CorrelationId,
            Reason: result.Reason);

    private AccessResult Evaluate(UserAccount actor, AccessRequest request)
    {
        if (actor.IsRevoked)
        {
            return new AccessResult(false, "credential-revoked", FreshnessLabel.NotApplicable);
        }

        // Scope is derived from the actor's own organization/branch grant, never
        // from the caller-submitted target — cross-tenant requests are denied
        // without revealing whether the target record exists.
        if (request.TargetOrganizationId != actor.OrganizationId
            || !actor.BranchScope.Contains(request.TargetBranchId))
        {
            return new AccessResult(false, "not-found", FreshnessLabel.NotApplicable);
        }

        if (request.Action.RequiredPermission != Permission.None
            && !actor.EffectivePermissions.HasFlag(request.Action.RequiredPermission))
        {
            return new AccessResult(false, "insufficient-permission", FreshnessLabel.NotApplicable);
        }

        if (request.IsOffline && request.Action.RequiresElevatedOfflinePermission)
        {
            return EvaluateOfflineFreshness(actor);
        }

        return new AccessResult(true, "allowed", FreshnessLabel.NotApplicable);
    }

    private AccessResult EvaluateOfflineFreshness(UserAccount actor)
    {
        var snapshot = actor.CachedAdminSnapshot;
        if (snapshot is null || snapshot.IsStale(_clock(), _offlineFreshnessWindow))
        {
            return new AccessResult(false, "offline-snapshot-stale", FreshnessLabel.Stale);
        }

        return new AccessResult(true, "allowed-via-cached-snapshot", FreshnessLabel.Fresh);
    }
}
