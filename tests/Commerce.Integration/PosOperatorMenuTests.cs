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

    // ---- Which sign-in screen ----------------------------------------------------

    [Fact]
    public void SignInRoute_UsesThePinPicker_WhenAnOperatorIsCachedAndFresh()
    {
        Assert.Equal(OperatorSignInScreen.PinPicker, OperatorSignInPlanner.ScreenFor([Op("a@x.test")], Now));
    }

    [Fact]
    public void SignInRoute_FallsBackToProvisioning_WhenNoFreshOperatorExists()
    {
        Assert.Equal(OperatorSignInScreen.Provision, OperatorSignInPlanner.ScreenFor([], Now));
        Assert.Equal(OperatorSignInScreen.Provision, OperatorSignInPlanner.ScreenFor([Op("a@x.test", ageDays: 30)], Now));
    }

    // ---- Session actions -----------------------------------------------------------

    private sealed class Prompts
    {
        public readonly List<OperatorLoginMode> Asked = [];
        public CachedOperator? Answer;

        public CachedOperator? Ask(OperatorLoginMode mode)
        {
            Asked.Add(mode);
            return Answer;
        }
    }

    [Fact]
    public void SwitchOperator_AsksForAPinPickerOnly_AndAdoptsTheChosenOperator()
    {
        var current = new CurrentOperator();
        var a = Op("a@x.test");
        var b = Op("b@x.test");
        current.Set(a);
        var prompts = new Prompts { Answer = b };

        new OperatorSessionActions(current, prompts.Ask).SwitchOperator();

        Assert.Equal([OperatorLoginMode.PinPicker], prompts.Asked);
        Assert.Equal(b, current.Value);
    }

    [Fact]
    public void SwitchOperator_Cancelled_KeepsTheCurrentOperator()
    {
        var current = new CurrentOperator();
        var a = Op("a@x.test");
        current.Set(a);

        new OperatorSessionActions(current, new Prompts().Ask).SwitchOperator();

        Assert.Equal(a, current.Value);
    }

    [Fact]
    public void SignOut_ClearsTheOperator_AndThenOffersThePinPicker()
    {
        var current = new CurrentOperator();
        current.Set(Op("a@x.test"));
        var prompts = new Prompts();

        new OperatorSessionActions(current, prompts.Ask).SignOut();

        Assert.Null(current.Value);
        Assert.Equal([OperatorLoginMode.PinPicker], prompts.Asked);
    }

    [Fact]
    public void SignOut_ThenSigningInAsAnotherOperator_AdoptsThem()
    {
        var current = new CurrentOperator();
        current.Set(Op("a@x.test"));
        var b = Op("b@x.test");

        new OperatorSessionActions(current, new Prompts { Answer = b }.Ask).SignOut();

        Assert.Equal(b, current.Value);
    }

    [Fact]
    public void SignIn_AsksForThePickerOrFirstRunProvisioning()
    {
        var current = new CurrentOperator();
        var prompts = new Prompts { Answer = Op("a@x.test") };

        new OperatorSessionActions(current, prompts.Ask).SignIn();

        Assert.Equal([OperatorLoginMode.PinPickerOrFirstRun], prompts.Asked);
        Assert.NotNull(current.Value);
    }

    [Fact]
    public void Reconcile_SignsOut_AnOperatorRemovedFromThisTerminal()
    {
        var current = new CurrentOperator();
        var a = Op("a@x.test");
        current.Set(a);

        new OperatorSessionActions(current, new Prompts().Ask).Reconcile([Op("b@x.test")]);

        Assert.Null(current.Value);
    }

    [Fact]
    public void Reconcile_RefreshesTheActiveOperator_WhenItWasProvisionedAgain()
    {
        var current = new CurrentOperator();
        var before = Op("a@x.test", permissions: (int)Permission.OperatePos);
        current.Set(before);
        var after = before with { Permissions = (int)(Permission.OperatePos | Permission.ManageUsers) };

        new OperatorSessionActions(current, new Prompts().Ask).Reconcile([after]);

        Assert.Equal(after, current.Value);
    }
}
