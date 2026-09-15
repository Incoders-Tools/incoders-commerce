namespace Commerce.Updater;

/// <summary>
/// ADR-005: packaging is conditional behind one manifest — MSIX where the
/// fleet supports it, signed MSI otherwise. Both paths are signed and
/// verified identically per ADR-004.
/// </summary>
public enum PackageFormat
{
    Msi,
    Msix
}

/// <summary>
/// A candidate release artifact as observed by the updater before trust
/// decisions are made. <see cref="SignatureValid"/>/<see cref="IsAttested"/>
/// are the outcome of a lower-layer signature/attestation check (out of
/// scope here per design.md's threat matrix — the updater never shells out,
/// it consumes typed verification results).
/// </summary>
public sealed record ReleasePackage(
    string PackagePath,
    string PublisherId,
    PackageFormat Format,
    string ContentHash,
    bool SignatureValid,
    bool IsAttested,
    int AppVersion,
    int SyncContractVersion,
    int SchemaVersion);

/// <summary>
/// The trusted, immutable-release-derived expectation an observed
/// <see cref="ReleasePackage"/> is checked against (ADR-004).
/// </summary>
public sealed record ReleaseManifest(
    string PublisherId,
    PackageFormat Format,
    string ContentHash);

/// <summary>Current installation's own app/sync/schema versions.</summary>
public sealed record InstallationState(
    int AppVersion,
    int SyncContractVersion,
    int SchemaVersion);
