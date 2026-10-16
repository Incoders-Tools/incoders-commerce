namespace Commerce.Updater;

/// <summary>
/// Fail-closed N/N+1 compatibility across app, sync-contract, and schema
/// versions (design.md, ADR-004). Any other relation — same major gap
/// skipped, or a downgrade attempted outside the explicit recovery path —
/// is rejected rather than assumed safe.
/// </summary>
public static class CompatibilityChecker
{
    public static VerificationResult Check(ReleasePackage package, InstallationState current) =>
        CheckVersions(package.AppVersion, package.SyncContractVersion, package.SchemaVersion, current);

    public static VerificationResult CheckVersions(int appVersion, int syncContractVersion, int schemaVersion, InstallationState current)
    {
        if (!IsNOrNPlusOne(current.AppVersion, appVersion)
            || !IsNOrNPlusOne(current.SyncContractVersion, syncContractVersion)
            || !IsNOrNPlusOne(current.SchemaVersion, schemaVersion))
        {
            return new VerificationResult(false, UpgradeRejectionReason.IncompatibleVersion,
                "Package is not N/N+1 compatible with the installation's app/sync/schema versions.");
        }

        return new VerificationResult(true, null, "Compatible.");
    }

    private static bool IsNOrNPlusOne(int current, int candidate) => candidate == current || candidate == current + 1;
}
