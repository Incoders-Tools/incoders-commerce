namespace Commerce.Updater;

/// <summary>The stages of the POS install wizard, in execution order.</summary>
public enum UpdateInstallStage
{
    Preflight,
    Download,
    Verify,
    Quiesce,
    Backup,
    Install
}

/// <summary>Typed reason an install did not proceed; never collapsed into a generic failure.</summary>
public enum UpdateFailureReason
{
    UnsupportedPackage,
    PublisherNotTrusted,
    DownloadFailed,
    HashMismatch,
    SignatureInvalid,
    SignerMismatch,
    SaleInProgress,
    SafeWindowUnavailable,
    NotPackaged,
    BackupFailed,
    InstallFailed,
    Cancelled
}

/// <param name="Message">Operator-facing Spanish text.</param>
public sealed record UpdateInstallOutcome(
    bool Succeeded,
    UpdateFailureReason? Reason,
    UpdateInstallStage Stage,
    string Message,
    string? BackupPath = null);

public sealed record UpdateProgress(UpdateInstallStage Stage, double? Fraction, string Message);

public sealed record UpdateInstallOptions(
    string StagingDirectory,
    string DatabasePath,
    string BackupDirectory,
    string TrustedPublisher,
    TimeSpan QuiesceTimeout);

public enum PackageSignatureStatus
{
    Valid,
    NotSigned,
    Invalid,

    /// <summary>The signature is intact but its certificate chain is not trusted on this machine.</summary>
    UntrustedRoot,
    Unknown
}

public sealed record PackageSignature(PackageSignatureStatus Status, string? SignerSubject, string? Detail = null);

/// <summary>Reads the Authenticode/MSIX signature of a staged package (typed result, never a shell call).</summary>
public interface IPackageSignatureVerifier
{
    PackageSignature Inspect(string packagePath);
}

/// <summary>Whether this process runs with package identity (an installed MSIX) or unpackaged (dev run).</summary>
public interface IPackagedAppInfo
{
    bool IsPackaged { get; }
}

public sealed record UpdateInstallerResult(bool Succeeded, string? Detail = null);

/// <summary>Installs a verified package through the OS deployment API.</summary>
public interface IUpdateInstaller
{
    Task<UpdateInstallerResult> InstallAsync(string packagePath, CancellationToken cancellationToken);
}

/// <summary>
/// Registers the running (non-UWP) process for restart after the package update
/// shuts it down, as required for packaged Win32 apps.
/// </summary>
public interface IApplicationRestartRegistrar
{
    bool Register();
}
