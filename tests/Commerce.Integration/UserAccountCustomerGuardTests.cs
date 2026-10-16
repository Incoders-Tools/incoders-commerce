using Commerce.Domain.Identity;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity task 2.1 (user-credentials spec
/// "Customer-Linked User Is Denied Staff Permissions"): a `UserAccount`
/// carrying a non-null `CustomerId` MUST report `Permission.None` regardless
/// of any `Role` rows assigned to it — the guard is enforced by construction
/// in `EffectivePermissions` itself, not by convention. No I/O.
/// </summary>
public sealed class UserAccountCustomerGuardTests
{
    [Fact]
    public void EffectivePermissions_CustomerIdBearingAccount_WithStaffRoles_ReportsNone()
    {
        var role = new Role(RoleCatalog.BusinessAdmin, Permission.ViewSales | Permission.ManageUsers | Permission.ManageCatalog);
        var account = new UserAccount(Guid.NewGuid(), Guid.NewGuid(), [], [role], customerId: Guid.NewGuid());

        Assert.Equal(Permission.None, account.EffectivePermissions);
    }

    [Fact]
    public void EffectivePermissions_AccountWithoutCustomerId_ReportsFullRoleSet_Unaffected()
    {
        var role = new Role(RoleCatalog.BusinessAdmin, Permission.ViewSales | Permission.ManageUsers);
        var account = new UserAccount(Guid.NewGuid(), Guid.NewGuid(), [], [role]);

        Assert.Equal(Permission.ViewSales | Permission.ManageUsers, account.EffectivePermissions);
        Assert.Null(account.CustomerId);
    }
}
