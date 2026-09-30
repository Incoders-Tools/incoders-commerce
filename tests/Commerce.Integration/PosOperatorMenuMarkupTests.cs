using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// Structural checks for the operator menu and the move of operator
/// provisioning into "Personal": the nav button opens a palette-themed menu,
/// the login window is a PIN picker that can no longer provision, and
/// provisioning is reachable from Personal.
/// </summary>
public sealed class PosOperatorMenuMarkupTests
{
    private static string Src(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Commerce.Pos.Windows", .. path]));
    }

    [Fact]
    public void NavBar_OperatorButton_OpensAMenuWithTheThreeActions_ThemedByPaletteKeys()
    {
        var xaml = Src("Controls", "PosNavBar.xaml");
        var code = Src("Controls", "PosNavBar.xaml.cs");

        Assert.Contains("x:Name=\"SwitchOperatorButton\"", xaml);
        Assert.Contains("x:Name=\"OperatorMenuPopup\"", xaml);
        Assert.Contains("x:Name=\"OperatorMenuTitle\"", xaml);
        Assert.Contains("x:Name=\"OperatorMenuSubtitle\"", xaml);
        Assert.Contains("x:Name=\"SwitchOperatorMenuButton\"", xaml);
        Assert.Contains("x:Name=\"SignOutMenuButton\"", xaml);
        Assert.Contains("x:Name=\"SignInMenuButton\"", xaml);
        Assert.Contains("Cambiar operador", xaml);
        Assert.Contains("Cerrar sesión", xaml);
        Assert.Contains("Iniciar sesión", xaml);
        Assert.Contains("OperatorMenuRequested", code);
        Assert.Contains("SignOutRequested", code);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Fact]
    public void NavBar_NeverOffersToAddAnOperator()
    {
        Assert.DoesNotContain("Agregar", Src("Controls", "PosNavBar.xaml"));
    }

    [Fact]
    public void MainWindow_RoutesTheMenuThroughTheSessionActions()
    {
        var xaml = Src("MainWindow.xaml");
        var code = Src("MainWindow.xaml.cs");

        Assert.Contains("OperatorMenuRequested=", xaml);
        Assert.Contains("SignOutRequested=", xaml);
        Assert.Contains("OperatorSessionActions", code);
        Assert.Contains("OperatorMenuPresenter.Build", code);
        Assert.DoesNotContain("ProvisionOperatorWindow", code);
    }

    [Fact]
    public void LoginWindow_IsAPinPickerOnly()
    {
        var xaml = Src("OperatorLoginWindow.xaml");
        var code = Src("OperatorLoginWindow.xaml.cs");

        Assert.Contains("x:Name=\"OperatorListBox\"", xaml);
        Assert.Contains("x:Name=\"PinBox\"", xaml);
        Assert.Contains("x:Name=\"ContinueWithoutOperatorButton\"", xaml);
        Assert.DoesNotContain("Agregar", xaml);
        Assert.DoesNotContain("EmailTextBox", xaml);
        Assert.DoesNotContain("ProvisionButton", xaml);
        Assert.DoesNotContain("OperatorProvisioningClient", code);
    }

    [Fact]
    public void ProvisionWindow_HoldsTheProvisioningForm_WithBusyHandling()
    {
        var xaml = Src("ProvisionOperatorWindow.xaml");
        var code = Src("ProvisionOperatorWindow.xaml.cs");

        foreach (var name in new[] { "EmailTextBox", "PasswordBox", "NewPinBox", "ConfirmPinBox", "ProvisionButton", "FormPanel", "BusyPanel" })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        Assert.Contains("Agregar operador", xaml);
        Assert.Contains("_busy.RunAsync", code);
        Assert.Contains("OperatorProvisioningClient", code);
        Assert.Contains("_operatorStore.Upsert", code);
    }

    [Fact]
    public void Personal_OffersTheTerminalOperators_WithRemoveOnly_NoProvisioning()
    {
        var staff = Src("StaffView.xaml");
        var staffCode = Src("StaffView.xaml.cs");

        Assert.Contains("x:Name=\"OperatorsItemsControl\"", staff);
        Assert.Contains("Operadores de esta terminal", staff);
        Assert.Contains("Quitar de esta terminal", staff);
        Assert.Contains("_operatorStore.Remove", staffCode);
        Assert.DoesNotContain("Agregar operador", staff);
        Assert.DoesNotContain("ProvisionOperatorWindow", staffCode);
        Assert.Empty(Regex.Matches(staff, @"{StaticResources+w*Brushw*}"));
    }

    [Fact]
    public void Startup_StillAllowsFirstRunProvisioning()
    {
        var code = Src("App.xaml.cs");

        Assert.Contains("OperatorSignInFlow", code);
    }
}
