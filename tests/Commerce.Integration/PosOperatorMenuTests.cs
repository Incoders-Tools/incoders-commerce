using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The logged-operator button opens a menu (who is signed in, switch operator,
/// sign out) instead of the provisioning window. The decisions behind it are
/// plain units: what the menu shows, which sign-in screen applies, and what
/// signing out does to <see cref="CurrentOperator"/>.
/// </summary>
public sealed class PosOperatorMenuTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static CachedOperator Op(string email, int permissions = (int)Permission.OperatePos, int ageDays = 1) =>
        new(Guid.NewGuid(), email, Guid.NewGuid(), [1], [2], Now.AddDays(-ageDays), permissions);

    // ---- Menu state ------------------------------------------------------------

    [Fact]
    public void Menu_WithAnActiveOperator_ShowsTheEmailTheRoleAndSignOut()
    {
        var cashier = Op("caja@vacaverde.test");

        var menu = OperatorMenuPresenter.Build(cashier, [cashier], Now);

        Assert.Equal("caja@vacaverde.test", menu.Title);
        Assert.Equal("Cajero", menu.Subtitle);
        Assert.Contains(OperatorMenuAction.SignOut, menu.Actions);
        Assert.DoesNotContain(OperatorMenuAction.SignIn, menu.Actions);
    }

    [Fact]
    public void Menu_OffersSwitchOperator_OnlyWhenAnotherOperatorIsCachedAndNotStale()
    {
        var active = Op("a@x.test");
        var other = Op("b@x.test");
        var staleOther = Op("c@x.test", ageDays: 30);

        Assert.DoesNotContain(OperatorMenuAction.SwitchOperator, OperatorMenuPresenter.Build(active, [active], Now).Actions);
        Assert.DoesNotContain(OperatorMenuAction.SwitchOperator, OperatorMenuPresenter.Build(active, [active, staleOther], Now).Actions);
        Assert.Contains(OperatorMenuAction.SwitchOperator, OperatorMenuPresenter.Build(active, [active, other], Now).Actions);
    }

    [Fact]
    public void Menu_WithoutAnActiveOperator_OffersOnlySignIn()
    {
        var menu = OperatorMenuPresenter.Build(null, [Op("a@x.test")], Now);

        Assert.Equal("Sin operador activo", menu.Title);
        Assert.Equal([OperatorMenuAction.SignIn], menu.Actions);
    }

    [Theory]
    [InlineData((int)Permission.OperatePos, "Cajero")]
    [InlineData((int)(Permission.OperatePos | Permission.ManageUsers | Permission.ViewSales), "Administrador")]
    [InlineData((int)Permission.ViewSales, "Sin acceso al punto de venta")]
    [InlineData(0, "Sin acceso al punto de venta")]
    public void RoleSummary_NamesTheOperatorsLevelOfAccess(int permissions, string expected)
    {
        Assert.Equal(expected, OperatorMenuPresenter.RoleSummary(permissions));
    }

    [Theory]
    [InlineData(OperatorMenuAction.SwitchOperator, "Cambiar operador")]
    [InlineData(OperatorMenuAction.SignOut, "Cerrar sesión")]
    [InlineData(OperatorMenuAction.SignIn, "Iniciar sesión")]
    public void ActionLabels_AreSpanish(OperatorMenuAction action, string label)
    {
        Assert.Equal(label, OperatorMenuPresenter.Label(action));
    }

    // ---- Session actions -----------------------------------------------------------
    // Signing out and switching operator both end in the lock screen: the session just
    // forgets the operator, and the lock layer (driven by CurrentOperator) does the rest.

    [Fact]
    public void SwitchOperator_ClearsTheOperator_SoTheLockScreenTakesOver()
    {
        var current = new CurrentOperator();
        current.Set(Op("a@x.test"));

        new OperatorSessionActions(current).SwitchOperator();

        Assert.Null(current.Value);
    }

    [Fact]
    public void SignOut_ClearsTheOperator()
    {
        var current = new CurrentOperator();
        current.Set(Op("a@x.test"));

        new OperatorSessionActions(current).SignOut();

        Assert.Null(current.Value);
    }

    [Fact]
    public void Reconcile_SignsOut_AnOperatorRemovedFromThisTerminal()
    {
        var current = new CurrentOperator();
        var a = Op("a@x.test");
        current.Set(a);

        new OperatorSessionActions(current).Reconcile([Op("b@x.test")]);

        Assert.Null(current.Value);
    }

    [Fact]
    public void Reconcile_RefreshesTheActiveOperator_WhenItsPermissionsChanged()
    {
        var current = new CurrentOperator();
        var before = Op("a@x.test", permissions: (int)Permission.OperatePos);
        current.Set(before);
        var after = before with { Permissions = (int)(Permission.OperatePos | Permission.ManageUsers) };

        new OperatorSessionActions(current).Reconcile([after]);

        Assert.Equal(after, current.Value);
    }

    [Fact]
    public void Reconcile_KeepsTheSameInstance_WhenNothingRelevantChanged()
    {
        // Runs after every sync: it must not look like a change (byte[] members compare by reference).
        var current = new CurrentOperator();
        var a = Op("a@x.test");
        current.Set(a);
        var reloaded = a with { Salt = [1], Subkey = [2], LastVerifiedUtc = Now };

        new OperatorSessionActions(current).Reconcile([reloaded]);

        Assert.Same(a, current.Value);
    }
}
