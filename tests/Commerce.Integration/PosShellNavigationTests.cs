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

    // ---- a hung teardown never blocks navigation for good ------------------------------------

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (ShellNavigation Shell, FakeClock Clock) PendingTeardown()
    {
        var clock = new FakeClock();
        var shell = new ShellNavigation(clock, TimeSpan.FromSeconds(5));
        shell.Navigate(ShellSection.Staff, Admin);
        shell.Reconcile(null, sectionBusy: true);
        return (shell, clock);
    }

    [Fact]
    public void Navigate_WhileTheTeardownIsPendingAndWithinTheTimeout_IsRefused()
    {
        var (shell, clock) = PendingTeardown();
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.True(shell.TeardownPending);
        Assert.False(shell.TeardownExpired);
        Assert.False(shell.Navigate(ShellSection.Customers, Admin));
    }

    [Fact]
    public void Navigate_AfterTheTeardownTimedOut_IsAllowed_SoAHungRequestNeverBlocksForGood()
    {
        var (shell, clock) = PendingTeardown();
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.False(shell.TeardownPending);
        Assert.True(shell.TeardownExpired);
        Assert.True(shell.Navigate(ShellSection.Customers, Admin));
        Assert.Equal(ShellSection.Customers, shell.Current);
        Assert.True(shell.CompleteTeardown());
        Assert.False(shell.TeardownExpired);
    }

    [Fact]
    public void TheDefaultTeardownTimeout_IsBounded()
    {
        Assert.InRange(ShellNavigation.DefaultTeardownTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    }

    // ---- section lifecycle: disposal is exactly once ----------------------------------------------

    private sealed class FakeSection : ISectionView
    {
        public bool IsBusy { get; set; }

        public int Disposed { get; private set; }

        public int Cancelled { get; private set; }

        public event Action? Idle;

        public void CancelPending() => Cancelled++;

        public void Dispose() => Disposed++;

        public void EndRequest()
        {
            IsBusy = false;
            Idle?.Invoke();
        }
    }

    [Fact]
    public void Show_ANewSection_DisposesThePreviousOneOnce()
    {
        var lifecycle = new SectionLifecycle();
        var first = new FakeSection();
        lifecycle.Show(first);

        lifecycle.Show(new FakeSection());
        lifecycle.Show(null);

        Assert.Equal(1, first.Disposed);
    }

    [Fact]
    public void DetachActive_CancelsTheRequest_AndDisposesTheViewExactlyOnce_WhenItGoesIdle()
    {
        var lifecycle = new SectionLifecycle();
        var view = new FakeSection { IsBusy = true };
        lifecycle.Show(view);
        var released = new List<bool>();
        lifecycle.DetachedReleased += released.Add;

        lifecycle.DetachActive();
        Assert.Null(lifecycle.Active);
        Assert.Equal(1, view.Cancelled);
        Assert.Equal(0, view.Disposed);

        view.EndRequest();
        view.EndRequest();
        lifecycle.ReleaseDetached();

        Assert.Equal(1, view.Disposed);
        Assert.Equal([true], released);
    }

    [Fact]
    public void ReleasingTheDetachedView_WhileANewSectionIsShown_DoesNotAskToClearTheHost()
    {
        var lifecycle = new SectionLifecycle();
        var old = new FakeSection { IsBusy = true };
        lifecycle.Show(old);
        var released = new List<bool>();
        lifecycle.DetachedReleased += released.Add;
        lifecycle.DetachActive();

        var next = new FakeSection();
        lifecycle.Show(next);
        old.EndRequest();

        Assert.Equal([false], released);
        Assert.Equal(1, old.Disposed);
        Assert.Equal(0, next.Disposed);
        Assert.Same(next, lifecycle.Active);
    }

    [Fact]
    public void ForcedRelease_OfAHungDetachedView_DisposesItOnce_AndALateIdleDoesNothing()
    {
        var lifecycle = new SectionLifecycle();
        var hung = new FakeSection { IsBusy = true };
        lifecycle.Show(hung);
        lifecycle.DetachActive();

        Assert.True(lifecycle.ReleaseDetached());
        hung.EndRequest();

        Assert.Equal(1, hung.Disposed);
        Assert.False(lifecycle.ReleaseDetached());
    }

    [Fact]
    public void TheWindow_ExplainsARefusedNavigation_AndReleasesAnExpiredTeardown()
    {
        var code = File.ReadAllText(Path.Combine(PosDir(), "MainWindow.xaml.cs"));

        Assert.Contains("PosMessages.PreviousOperationRunning", code);
        Assert.Contains("TeardownExpired", code);
        Assert.Contains("_sections.DetachActive()", code);
        Assert.False(string.IsNullOrWhiteSpace(Commerce.Pos.Windows.PosMessages.PreviousOperationRunning));
    }

    private static string PosDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Commerce.Pos.Windows");
    }
}
