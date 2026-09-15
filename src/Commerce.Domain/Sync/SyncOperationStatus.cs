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
    Denied
}

public sealed record InboundApplyResult(InboundApplyOutcome Outcome, Guid OperationId);
