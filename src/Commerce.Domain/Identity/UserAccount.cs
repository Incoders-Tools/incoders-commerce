namespace Commerce.Domain.Identity;

public sealed class UserAccount
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public IReadOnlySet<Guid> BranchScope { get; }
    public IReadOnlyList<Role> Roles { get; }
    public bool IsRevoked { get; private set; }
    public AdminPermissionSnapshot? CachedAdminSnapshot { get; private set; }

    public UserAccount(
        Guid id,
        Guid organizationId,
        IEnumerable<Guid> branchScope,
        IEnumerable<Role> roles)
    {
        Id = id;
        OrganizationId = organizationId;
        BranchScope = branchScope.ToHashSet();
        Roles = roles.ToList();
    }

    public Permission EffectivePermissions =>
        Roles.Aggregate(Permission.None, (acc, role) => acc | role.Permissions);

    public void Revoke() => IsRevoked = true;

    public void CacheAdminSnapshot(AdminPermissionSnapshot snapshot) =>
        CachedAdminSnapshot = snapshot;
}
