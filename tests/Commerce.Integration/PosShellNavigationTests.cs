using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The shell navigation model: which sections an operator may open, how a
/// section switch is decided, and what happens when the operator changes while
/// a section is open. UI-free; the window only mirrors the model.
/// </summary>
public sealed class PosShellNavigationTests
{
    private const int Cashier = (int)Permission.OperatePos;
    private const int Admin = (int)(Permission.OperatePos | Permission.ManageUsers);

    [Fact]
    public void Starts_OnTheSale()
    {
        Assert.Equal(ShellSection.Sale, new ShellNavigation().Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Cashier)]
    public void WithoutManageUsers_OnlyTheSaleIsAllowed(int? permissions)
    {
        Assert.Equal([ShellSection.Sale], ShellNavigation.Allowed(permissions));
    }

    [Fact]
    public void WithManageUsers_CustomersAndStaffAreAllowedToo()
    {
        Assert.Equal([ShellSection.Sale, ShellSection.Customers, ShellSection.Staff], ShellNavigation.Allowed(Admin));
    }

    [Fact]
    public void Navigate_ToAnAllowedSection_Switches_AndBackToTheSale()
    {
        var shell = new ShellNavigation();

        Assert.True(shell.Navigate(ShellSection.Staff, Admin));
        Assert.Equal(ShellSection.Staff, shell.Current);
        Assert.True(shell.Navigate(ShellSection.Sale, Admin));
        Assert.Equal(ShellSection.Sale, shell.Current);
    }

    [Fact]
    public void Navigate_ToTheCurrentSection_ReportsNoChange()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Customers, Admin);

        Assert.False(shell.Navigate(ShellSection.Customers, Admin));
    }

    [Fact]
    public void Navigate_ToADisallowedSection_IsRefused_AndKeepsTheCurrentOne()
    {
        var shell = new ShellNavigation();

        Assert.False(shell.Navigate(ShellSection.Staff, Cashier));
        Assert.Equal(ShellSection.Sale, shell.Current);
    }

    [Theory]
    [InlineData(Cashier)]
    [InlineData(null)]
    public void Reconcile_AfterTheOperatorLosesAccess_FallsBackToTheSale(int? permissions)
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);

        Assert.True(shell.Reconcile(permissions));
        Assert.Equal(ShellSection.Sale, shell.Current);
    }

    [Fact]
    public void Reconcile_WhileStillAllowed_ChangesNothing()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);

        Assert.False(shell.Reconcile(Admin));
        Assert.Equal(ShellSection.Staff, shell.Current);
    }
}
