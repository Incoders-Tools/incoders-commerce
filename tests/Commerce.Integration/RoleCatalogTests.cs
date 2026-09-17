using Commerce.Domain.Identity;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy tasks 1.1/1.2: the canonical server-side
/// role catalog (proposal.md "Canonical server-side role catalog" /
/// design.md "RoleCatalog shape"). Pure, no I/O.
/// </summary>
public sealed class RoleCatalogTests
{
    [Theory]
    [InlineData(
        RoleCatalog.BusinessAdmin,
        Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]
    [InlineData(RoleCatalog.Seller, Permission.ViewSales)]
    [InlineData(RoleCatalog.Provider, Permission.None)]
    [InlineData(RoleCatalog.PlatformAdmin, Permission.None)]
    public void TryResolve_CanonicalName_ResolvesExactPermissionSet(string name, Permission expected)
    {
        var resolved = RoleCatalog.TryResolve(name, out var role);

        Assert.True(resolved);
        Assert.Equal(name, role!.Name);
        Assert.Equal(expected, role.Permissions);
    }

    [Fact]
    public void TryResolve_UnknownName_ReturnsFalse()
    {
        var resolved = RoleCatalog.TryResolve("Vendedor", out var role);

        Assert.False(resolved);
        Assert.Null(role);
    }

    [Fact]
    public void TryResolve_IsCaseInsensitive()
    {
        var resolved = RoleCatalog.TryResolve("Seller", out var role);

        Assert.True(resolved);
        Assert.Equal(RoleCatalog.Seller, role!.Name);
    }

    [Fact]
    public void OrgAssignable_ExcludesPlatformAdmin()
    {
        Assert.DoesNotContain(RoleCatalog.PlatformAdmin, RoleCatalog.OrgAssignable);
        Assert.Contains(RoleCatalog.BusinessAdmin, RoleCatalog.OrgAssignable);
        Assert.Contains(RoleCatalog.Seller, RoleCatalog.OrgAssignable);
        Assert.Contains(RoleCatalog.Provider, RoleCatalog.OrgAssignable);
    }
}
