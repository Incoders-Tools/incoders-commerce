namespace Commerce.Domain.Identity;

public sealed class UserAccount
{
    public Guid Id { get; }
    public Guid OrganizationId { get; }
    public IReadOnlySet<Guid> BranchScope { get; }
    public IReadOnlyList<Role> Roles { get; }
    public bool IsRevoked { get; private set; }
    public bool IsSystemAdmin { get; }
    public AdminPermissionSnapshot? CachedAdminSnapshot { get; private set; }

    /// <summary>
    /// Optional link to a <c>Customer</c> row in the same organization
    /// (commerce-customer-identity design.md "One identity plane"). A
    /// non-null value marks this account as a customer login, never a staff
    /// user — see <see cref="EffectivePermissions"/>.
    /// </summary>
    public Guid? CustomerId { get; }

    public UserAccount(
        Guid id,
        Guid organizationId,
        IEnumerable<Guid> branchScope,
        IEnumerable<Role> roles,
        Guid? customerId = null,
        bool isSystemAdmin = false)
    {
        Id = id;
        OrganizationId = organizationId;
        BranchScope = branchScope.ToHashSet();
        Roles = roles.ToList();
        CustomerId = customerId;
        IsSystemAdmin = isSystemAdmin;
    }

    /// <summary>
    /// Denied by construction, not by convention (commerce-customer-identity
    /// design.md "Staff-permission denial for a CustomerId-bearing user"): a
    /// customer login holds NO staff permission, ever, regardless of any
    /// <see cref="Role"/> rows recorded against this account — even a
    /// misconfiguration or a prior state that assigned roles cannot exercise
    /// them once <see cref="CustomerId"/> is set. This is the single choke
    /// point every authorization site in the repo already reads.
    /// </summary>
    public Permission EffectivePermissions =>
        CustomerId is null
            ? Roles.Aggregate(Permission.None, (acc, role) => acc | role.Permissions)
            : Permission.None;

    public void Revoke() => IsRevoked = true;

    public void CacheAdminSnapshot(AdminPermissionSnapshot snapshot) =>
        CachedAdminSnapshot = snapshot;
}
