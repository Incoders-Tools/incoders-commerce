namespace Commerce.Domain.Sync;

/// <summary>
/// Non-blocking synchronization visibility for a branch: last successful
/// cloud freshness plus pending/offline state, exposed without gating local
/// work on connectivity. <see cref="LastAcknowledgedUtc"/> moves only when something was SENT and confirmed;
/// <see cref="LastDownloadedUtc"/> is the last time the cloud data (prices, customers, balances) was applied here.
/// </summary>
public sealed record SyncStatusSnapshot(
    Guid BranchId,
    DateTimeOffset? LastAcknowledgedUtc,
    int PendingOperationCount,
    bool IsOffline,
    DateTimeOffset? LastDownloadedUtc = null);
