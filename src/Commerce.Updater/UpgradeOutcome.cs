namespace Commerce.Updater;

/// <summary>
/// The seven distinct, typed rejection categories ADR-004 requires — never
/// collapsed into a generic failure (design.md threat matrix).
/// </summary>
public enum UpgradeRejectionReason
{
    PathTraversal,
    PublisherOrPackageTypeMismatch,
    TamperedOrUnsignedPackage,
    IncompatibleVersion,
    InsufficientPrivilege,
    InterruptedUpgrade,
    BackupOrRecoveryFailure
}

public enum UpgradeStatus
{
    Applied,
    Rejected,
    WaitingForSafeWindow,
    RolledBackPreReopen,
    RolledBackPostReopen,
    ForwardRepaired
}

/// <summary>
/// <paramref name="RejectionReason"/> is populated both for hard rejections
/// (<see cref="UpgradeStatus.Rejected"/>) and for recovery outcomes that were
/// triggered by a detected interruption, so callers can distinguish "never
/// started" from "recovered from an interrupted upgrade".
/// </summary>
public sealed record UpgradeOutcome(
    UpgradeStatus Status,
    UpgradeRejectionReason? RejectionReason,
    string Reason,
    int PreservedOrCurrentVersion);
