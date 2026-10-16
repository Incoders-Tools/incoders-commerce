namespace Commerce.Domain.Sync;

public enum SyncOperationStatus
{
    Pending,
    Acknowledged
}

/// <summary>
/// Outcome of applying an inbound synchronization operation. Shared by both
/// branch-side (<c>Commerce.BranchNode</c>) and cloud-side
/// (<c>Commerce.Cloud.Api</c>) receivers so neither peer depends on the
/// other's assembly for this shape.
/// </summary>
public enum InboundApplyOutcome
{
    Applied,
    DuplicateIgnored,
    Denied,

    /// <summary>
    /// Phase F (commerce-sync-ownership design.md "Materialization contract"):
    /// no registered <c>IInboundEffectHandler</c> claims the envelope's
    /// <c>PayloadKind</c>. The whole transaction rolls back — no <c>inbox</c>
    /// row is written — so the sender retries after the receiver upgrades,
    /// instead of the envelope being silently swallowed as applied.
    /// </summary>
    UnknownKind
}

public sealed record InboundApplyResult(InboundApplyOutcome Outcome, Guid OperationId);
