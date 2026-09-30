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
    public void Reconcile_WhileTheSectionIsBusy_MovesTheModelToTheSaleAtOnce_AndMarksTheSectionForTeardown()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);

        Assert.Equal(ReconcileOutcome.Deferred, shell.Reconcile(null, sectionBusy: true));
        Assert.Equal(ShellSection.Sale, shell.Current);
        Assert.True(shell.TeardownPending);
    }

    [Fact]
    public void CompleteTeardown_OnceTheSectionIsIdle_ReleasesTheOldSection_AndReportsItOnce()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);
        shell.Reconcile(Cashier, sectionBusy: true);

        Assert.True(shell.CompleteTeardown());
        Assert.False(shell.TeardownPending);
        Assert.Equal(ShellSection.Sale, shell.Current);
        Assert.False(shell.CompleteTeardown());
    }

    [Fact]
    public void Navigate_WhileTheOldSectionAwaitsTeardown_IsRefused()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);
        shell.Reconcile(null, sectionBusy: true);

        Assert.False(shell.Navigate(ShellSection.Customers, Admin));
        Assert.Equal(ShellSection.Sale, shell.Current);
    }

    [Fact]
    public void DeferredSignOut_ThenANewAdminSignsInBeforeIdle_KeepsTheModelOnTheSale_UntilIdleDisposesTheOldSection()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);

        shell.Reconcile(null, sectionBusy: true);
        Assert.Equal(ReconcileOutcome.Unchanged, shell.Reconcile(Admin, sectionBusy: false));

        // The shell and the screen agree: the sale, with the old section still waiting to be torn down.
        Assert.Equal(ShellSection.Sale, shell.Current);
        Assert.True(shell.TeardownPending);

        Assert.True(shell.CompleteTeardown());
        Assert.Equal(ShellSection.Sale, shell.Current);
        Assert.True(shell.Navigate(ShellSection.Staff, Admin));
    }

    [Fact]
    public void Reconcile_WhileStillAllowed_IsUnchanged_EvenWhenBusy()
    {
        var shell = new ShellNavigation();
        shell.Navigate(ShellSection.Staff, Admin);

        Assert.Equal(ReconcileOutcome.Unchanged, shell.Reconcile(Admin, sectionBusy: true));
        Assert.False(shell.TeardownPending);
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
