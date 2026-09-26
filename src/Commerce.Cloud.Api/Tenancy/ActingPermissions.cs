using Commerce.Domain.Identity;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Permission elevation for a system administrator acting on a selected
/// organization (platform-administration spec "Sysadmin Acts On A Selected
/// Organization"). A system administrator's own persisted `users` row NEVER
/// carries roles (`platform-admin` is reserved and unassignable — see
/// commerce-role-taxonomy), so <see cref="UserAccount.EffectivePermissions"/>
/// alone would always deny every tenant-module permission check for that
/// row. This is the ONE place that grants the full staff permission set for
/// the DURATION of a request scoped to a selected organization
/// (<see cref="CloudTenantScope.IsActingOnSelectedOrganization"/>) — never
/// persisted, never a claim, never available to a non-sysadmin caller.
/// </summary>
public static class ActingPermissions
{
    /// <summary>
    /// Every permission any role can hold today — identical to
    /// `business-admin`'s set in <see cref="RoleCatalog"/>. Kept as its own
    /// constant (rather than aliasing `RoleCatalog`) so a future narrower
    /// role never silently changes what a sysadmin gets for free.
    /// </summary>
    public const Permission FullStaffPermissions =
        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings;

    /// <summary>
    /// The permission set to authorize a request against: the caller's own
    /// <see cref="UserAccount.EffectivePermissions"/>, UNLESS the caller is a
    /// system administrator acting on a selected organization, in which case
    /// it is <see cref="FullStaffPermissions"/> for that request only.
    /// </summary>
    public static Permission For(UserAccount caller, CloudTenantScope scope) =>
        caller.IsSystemAdmin && scope.IsActingOnSelectedOrganization
            ? FullStaffPermissions
            : caller.EffectivePermissions;

    /// <summary>
    /// A stand-in <see cref="UserAccount"/> for pure logic that reads
    /// <c>EffectivePermissions</c> directly (e.g. <see cref="RoleGrantPolicy"/>)
    /// and cannot itself be made scope-aware. Returns <paramref name="caller"/>
    /// unchanged unless elevation applies; otherwise a fresh, NEVER persisted
    /// instance carrying a synthetic full-permission role — same identity,
    /// branch scope, and system-admin flag as <paramref name="caller"/>.
    /// </summary>
    public static UserAccount EffectiveCaller(UserAccount caller, CloudTenantScope scope)
    {
        if (!caller.IsSystemAdmin || !scope.IsActingOnSelectedOrganization) return caller;

        var actingRole = new Role("system-administrator-acting", FullStaffPermissions);
        return new UserAccount(caller.Id, scope.OrganizationId, caller.BranchScope, [actingRole], caller.CustomerId, caller.IsSystemAdmin);
    }
}
