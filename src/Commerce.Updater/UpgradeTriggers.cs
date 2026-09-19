using Commerce.Application.Access;
using Commerce.Domain.Identity;

namespace Commerce.Updater;

/// <summary>
/// Thin adapter: an interactive CLI-triggered upgrade fails fast if it can't
/// reach a safe window quickly, rather than blocking an operator's terminal.
/// Contains no business/authorization logic of its own — matches the Units
/// 4-5 "thin adapter, one shared service" pattern.
/// </summary>
public sealed class CliUpgradeTrigger
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly UpdaterService _updater;

    public CliUpgradeTrigger(UpdaterService updater)
    {
        _updater = updater;
    }

    public UpgradeOutcome Trigger(
        UserAccount actor,
        AccessRequest accessContext,
        ReleasePackage package,
        ReleaseManifest trustedManifest,
        InstallationState currentState,
        string controlledStagingRoot,
        TimeSpan? timeout = null) =>
        _updater.RunUpgrade(actor, accessContext, package, trustedManifest, currentState, controlledStagingRoot, timeout ?? DefaultTimeout);
}

/// <summary>
/// Thin adapter: a background scheduled upgrade can afford to wait much
/// longer for a safe window before giving up. Same shared
/// <see cref="UpdaterService"/>, no business/authorization logic of its own.
/// </summary>
public sealed class ScheduledUpgradeTrigger
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly UpdaterService _updater;

    public ScheduledUpgradeTrigger(UpdaterService updater)
    {
        _updater = updater;
    }

    public UpgradeOutcome Trigger(
        UserAccount actor,
        AccessRequest accessContext,
        ReleasePackage package,
        ReleaseManifest trustedManifest,
        InstallationState currentState,
        string controlledStagingRoot,
        TimeSpan? timeout = null) =>
        _updater.RunUpgrade(actor, accessContext, package, trustedManifest, currentState, controlledStagingRoot, timeout ?? DefaultTimeout);
}
