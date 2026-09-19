namespace Commerce.Domain.Identity;

/// <summary>
/// Cached admin verifier/permission snapshot a branch keeps for offline
/// administrative actions, per ADR-002's explicit freshness policy.
/// Encryption-at-rest of the cache is an infrastructure concern outside this
/// walking skeleton.
/// </summary>
public sealed class AdminPermissionSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; }
    public Permission Permissions { get; }

    public AdminPermissionSnapshot(DateTimeOffset capturedAtUtc, Permission permissions)
    {
        CapturedAtUtc = capturedAtUtc;
        Permissions = permissions;
    }

    public bool IsStale(DateTimeOffset asOfUtc, TimeSpan freshnessWindow) =>
        asOfUtc - CapturedAtUtc > freshnessWindow;
}
