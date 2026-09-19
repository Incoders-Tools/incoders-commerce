namespace Commerce.Domain.Sync;

/// <summary>
/// Non-blocking synchronization visibility for a branch: last successful
/// cloud freshness plus pending/offline state, exposed without gating local
/// work on connectivity.
/// </summary>
public sealed record SyncStatusSnapshot(
    Guid BranchId,
    DateTimeOffset? LastAcknowledgedUtc,
    int PendingOperationCount,
    bool IsOffline);
