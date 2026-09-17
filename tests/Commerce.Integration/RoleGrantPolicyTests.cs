using Commerce.Domain.Identity;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy tasks 1.3/1.4: the pure grant-cap decision
/// (proposal.md "Grant cap" / design.md "Grant-cap location"). No I/O.
/// </summary>
public sealed class RoleGrantPolicyTests
{
    private static UserAccount Caller(params Role[] roles) =>
        new(Guid.NewGuid(), Guid.NewGuid(), [], roles);

    [Fact]
    public void TryAuthorize_SubsetGrant_IsAllowed()
    {
        var caller = Caller(new Role(RoleCatalog.BusinessAdmin, Permission.ViewSales | Permission.ManageUsers));

        var authorized = RoleGrantPolicy.TryAuthorize(caller, [RoleCatalog.Seller], out var roles, out var denial);

        Assert.True(authorized);
        Assert.Equal(GrantDenial.None, denial);
        Assert.Single(roles!);
        Assert.Equal(RoleCatalog.Seller, roles![0].Name);
    }

    [Fact]
    public void TryAuthorize_SupersetGrant_IsDenied_ExceedsCallerPermissions()
    {
        var caller = Caller(new Role(RoleCatalog.Seller, Permission.ViewSales));

        var authorized = RoleGrantPolicy.TryAuthorize(caller, [RoleCatalog.BusinessAdmin], out var roles, out var denial);

        Assert.False(authorized);
        Assert.Equal(GrantDenial.ExceedsCallerPermissions, denial);
        Assert.Null(roles);
    }

    [Fact]
    public void TryAuthorize_PlatformAdmin_IsDenied_ReservedRole_EvenForAllFlagsCaller()
    {
        var caller = Caller(new Role(
            RoleCatalog.BusinessAdmin,
            Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings));

        var authorized = RoleGrantPolicy.TryAuthorize(caller, [RoleCatalog.PlatformAdmin], out var roles, out var denial);

        Assert.False(authorized);
        Assert.Equal(GrantDenial.ReservedRole, denial);
        Assert.Null(roles);
    }

    [Fact]
    public void TryAuthorize_UnknownName_IsDenied_UnknownRole()
    {
        var caller = Caller(new Role(
            RoleCatalog.BusinessAdmin,
            Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings));

        var authorized = RoleGrantPolicy.TryAuthorize(caller, ["Vendedor"], out var roles, out var denial);

        Assert.False(authorized);
        Assert.Equal(GrantDenial.UnknownRole, denial);
        Assert.Null(roles);
    }

    [Fact]
    public void TryAuthorize_EmptyList_IsAllowed()
    {
        var caller = Caller(new Role(RoleCatalog.Seller, Permission.ViewSales));

        var authorized = RoleGrantPolicy.TryAuthorize(caller, [], out var roles, out var denial);

        Assert.True(authorized);
        Assert.Equal(GrantDenial.None, denial);
        Assert.Empty(roles!);
    }
}
