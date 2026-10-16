namespace Commerce.Updater;

/// <summary>Detected capabilities of the target Windows machine (ADR-005).</summary>
public sealed record WindowsFleetCapability(int BuildNumber, bool HasAdminInstallRights);

public sealed record FormatSelectionResult(bool Ok, PackageFormat? Format, UpgradeRejectionReason? Reason, string Detail);

/// <summary>
/// ADR-005: packaging is conditional behind one manifest. Administrative
/// installation is a fixed requirement of the Windows Service branch node
/// regardless of format, so a machine without admin rights fails closed
/// rather than silently picking a format it cannot install.
/// </summary>
public static class PackageFormatSelector
{
    private const int Windows10Version2004Build = 19041;

    public static FormatSelectionResult Select(WindowsFleetCapability capability)
    {
        if (!capability.HasAdminInstallRights)
        {
            return new FormatSelectionResult(false, null, UpgradeRejectionReason.InsufficientPrivilege,
                "Administrative installation rights are required for either packaging path.");
        }

        return capability.BuildNumber >= Windows10Version2004Build
            ? new FormatSelectionResult(true, PackageFormat.Msix, null, "Fleet meets the MSIX packaged-service requirements (Windows 10 2004+).")
            : new FormatSelectionResult(true, PackageFormat.Msi, null, "Fleet is below the Windows 10 2004 floor; falling back to signed MSI per ADR-005.");
    }
}
