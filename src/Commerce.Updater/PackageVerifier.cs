namespace Commerce.Updater;

public sealed record VerificationResult(bool Ok, UpgradeRejectionReason? Reason, string Detail);

/// <summary>
/// Fail-closed package verification per ADR-004: path containment, then
/// publisher/type, then signature/attestation/hash — in that order, each a
/// distinct typed rejection. Never touches the shell (design.md threat
/// matrix): all checks are on typed, already-collected fields.
/// </summary>
public static class PackageVerifier
{
    public static VerificationResult Verify(ReleasePackage package, ReleaseManifest trusted, string controlledStagingRoot)
    {
        var root = Path.GetFullPath(controlledStagingRoot);
        var candidatePath = Path.GetFullPath(Path.Combine(root, package.PackagePath));
        var isContained = candidatePath == root
            || candidatePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        if (!isContained)
        {
            return new VerificationResult(false, UpgradeRejectionReason.PathTraversal,
                $"Package path '{package.PackagePath}' escapes the controlled staging root.");
        }

        if (package.PublisherId != trusted.PublisherId || package.Format != trusted.Format)
        {
            return new VerificationResult(false, UpgradeRejectionReason.PublisherOrPackageTypeMismatch,
                "Publisher or package type does not match the trusted release manifest.");
        }

        if (!package.SignatureValid || !package.IsAttested || package.ContentHash != trusted.ContentHash)
        {
            return new VerificationResult(false, UpgradeRejectionReason.TamperedOrUnsignedPackage,
                "Package signature, attestation, or content hash verification failed.");
        }

        return new VerificationResult(true, null, "Package verified: contained, trusted publisher/type, signed and matching hash.");
    }
}
