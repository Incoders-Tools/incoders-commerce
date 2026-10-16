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

    /// <summary>The pending-upgrade marker could not be written, so nothing was installed.</summary>
    PendingMarkerFailed,

    /// <summary>An exception the workflow did not anticipate; carries the stage that was running.</summary>
    UnexpectedError,
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

/// <param name="TrustedThumbprint">
/// Optional pin: the SHA-256 thumbprint of the trusted signing certificate
/// (<c>Commerce:UpdateTrustedThumbprint</c>). When set, the signer must match
/// it exactly and <see cref="PackageSignatureStatus.UntrustedRoot"/> is
/// accepted for that signer only; when unset, only <c>Valid</c> plus the
/// publisher subject is accepted.
/// </param>
public sealed record UpdateInstallOptions(
    string StagingDirectory,
    string DatabasePath,
    string BackupDirectory,
    string TrustedPublisher,
    TimeSpan QuiesceTimeout,
    string? TrustedThumbprint = null);

public enum PackageSignatureStatus
{
    Valid,
    NotSigned,
    Invalid,

    /// <summary>The signature is intact but its certificate chain is not trusted on this machine.</summary>
    UntrustedRoot,

    /// <summary>The signing certificate has expired (signatures are not timestamped in the interim period).</summary>
    Expired,
    Unknown
}

/// <param name="SignerThumbprint">
/// SHA-256 of the signing certificate (uppercase hex, no separators), read from
/// the package signature block; null when it could not be read.
/// </param>
public sealed record PackageSignature(
    PackageSignatureStatus Status, string? SignerSubject, string? Detail = null, string? SignerThumbprint = null);

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
