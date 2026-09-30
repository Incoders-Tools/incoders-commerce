using System.IO;
using System.Runtime.InteropServices;
using Commerce.Updater;

namespace Commerce.Pos.Windows;

/// <summary>
/// Reports whether the POS runs with MSIX package identity. An unpackaged
/// (dev) run has none, and the wizard must stop before installing.
/// </summary>
public sealed class PackagedAppInfo : IPackagedAppInfo
{
    private const int ErrorInsufficientBuffer = 122;

    public bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result == ErrorInsufficientBuffer;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}

/// <summary>
/// Registers the process for restart so Windows relaunches the POS after the
/// package update shuts it down. Documented as required for non-UWP packaged
/// apps and must run before shutdown begins:
/// https://learn.microsoft.com/en-us/windows/msix/non-store-developer-updates
/// </summary>
public sealed class ApplicationRestartRegistrar : IApplicationRestartRegistrar
{
    public bool Register()
    {
        try
        {
            return RegisterApplicationRestart(null, 0) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterApplicationRestart(string? commandLine, uint flags);
}

/// <summary>
/// Installs the verified MSIX through the package deployment API. With
/// <c>ForceApplicationShutdown</c> Windows closes the running POS and applies
/// the update; the process is normally terminated before this returns.
/// Requires the <c>packageManagement</c> restricted capability declared in the
/// package manifest.
/// </summary>
public sealed class PackageManagerUpdateInstaller : IUpdateInstaller
{
    public async Task<UpdateInstallerResult> InstallAsync(string packagePath, CancellationToken cancellationToken)
    {
        var manager = new global::Windows.Management.Deployment.PackageManager();
        var operation = manager.AddPackageAsync(
            new Uri(Path.GetFullPath(packagePath)),
            null,
            global::Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown);

        using var registration = cancellationToken.Register(operation.Cancel);
        try
        {
            var result = await operation;
            return result.ExtendedErrorCode is null || result.ExtendedErrorCode.HResult == 0
                ? new UpdateInstallerResult(true)
                : new UpdateInstallerResult(false, $"{result.ExtendedErrorCode.Message} {result.ErrorText}".Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed deployment surfaces as an exception carrying the HRESULT.
            return new UpdateInstallerResult(false, $"0x{ex.HResult:X8} {ex.Message}".Trim());
        }
    }
}

/// <summary>
/// Builds the install workflow for one wizard run. The sale screen supplies the
/// "a sale is being built" probe, so the update is refused while the cart holds
/// lines.
/// </summary>
public sealed class UpdateInstallWorkflowFactory(
    PackageDownloader downloader,
    IPackageSignatureVerifier signatures,
    IUpgradeBackup backup,
    IBranchNodeQuiescence quiescence,
    IPackagedAppInfo packagedApp,
    IApplicationRestartRegistrar restartRegistrar,
    IUpdateInstaller installer,
    PendingUpgradeStore pendingStore,
    UpdateInstallOptions options)
{
    public string TrustedPublisher => options.TrustedPublisher;

    public bool IsPackaged => packagedApp.IsPackaged;

    public UpdateInstallWorkflow Create(Func<bool> isSaleInProgress) => new(
        downloader, signatures, backup, quiescence, packagedApp, restartRegistrar, installer, pendingStore, isSaleInProgress, options);
}
