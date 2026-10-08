using System.Text.RegularExpressions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Structural checks for the shell: the main window hosts one section at a time
/// (the sale is hidden, never rebuilt, so the cart survives), the nav bar has a
/// "Venta" entry and marks the active section, and Clientes / Personal are views
/// that scroll and show their status inline instead of modal windows.
/// </summary>
public sealed class PosShellMarkupTests
{
    private static string Src(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, "src", "Commerce.Pos.Windows", .. path]));
    }

    [Fact]
    public void MainWindow_HostsTheSectionsInTheContentArea_AndKeepsTheSaleScreenAlive()
    {
        var xaml = Src("MainWindow.xaml");
        var code = Src("MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"SectionHost\"", xaml);
        Assert.Contains("x:Name=\"SaleScreen\"", xaml);
        Assert.Contains("SaleRequested=", xaml);
        Assert.Contains("ShellNavigation", code);
        Assert.Contains("new CustomersView(", code);
        Assert.Contains("SaleScreen.Visibility", code);
        Assert.DoesNotContain("new CustomersWindow", code);
        // Switching sections must never rebuild or clear the sale (and the guard must actually find the methods).
        foreach (var method in new[] { "ShowSection", "ApplySection", "ReconcileShell" })
        {
            var body = Regex.Match(code, @"void " + method + @"\([^)]*\)[\s\S]*?\n    }");
            Assert.True(body.Success, $"{method} was not found in MainWindow.xaml.cs");
            Assert.DoesNotContain("_cart.Clear", body.Value);
        }
    }

    [Fact]
    public void MainWindow_NeverDisposesABusySection_WhenTheOperatorChanges()
    {
        var code = Src("MainWindow.xaml.cs");
        // Both ways out of a section (the operator lost it, or the server refused the operator) pass the busy flag
        // and share one outcome handler that detaches a busy section instead of disposing it.
        var reconcile = Regex.Match(code, @"void ReconcileShell\(\) =>[^;]*;");
        var refused = Regex.Match(code, @"void OnManagementOperatorRefused\(\)[\s\S]*?\n    }");
        var apply = Regex.Match(code, @"void ApplyShellOutcome\(ReconcileOutcome outcome\)[\s\S]*?\n    }");

        Assert.True(reconcile.Success);
        Assert.True(refused.Success);
        Assert.True(apply.Success);
        Assert.Contains("_sections.Active?.IsBusy == true", reconcile.Value);
        Assert.Contains("ApplyShellOutcome(", reconcile.Value);
        Assert.Contains("_sections.Active?.IsBusy == true", refused.Value);
        Assert.Contains("ApplyShellOutcome(", refused.Value);
        Assert.Contains("ReconcileOutcome.Deferred", apply.Value);
        Assert.Contains("_sections.DetachActive()", apply.Value);

        // The window never shows a section the model has left: the idle handler only tears down.
        var idle = Regex.Match(code, @"void OnDetachedSectionReleased\(bool clearHost\)[\s\S]*?\n    }");
        Assert.True(idle.Success);
        Assert.Contains("CompleteTeardown", idle.Value);
        Assert.DoesNotContain("SectionHost.Visibility = Visibility.Visible", idle.Value);
    }

    [Fact]
    public void NavBar_HasAVentaEntry_AndMarksTheActiveSection()
    {
        var xaml = Src("Controls", "PosNavBar.xaml");
        var code = Src("Controls", "PosNavBar.xaml.cs");

        Assert.Contains("x:Name=\"NewSaleButton\"", xaml);
        Assert.Contains("Text=\"Venta\"", xaml);
        Assert.DoesNotContain("Nueva Venta", xaml);
        Assert.Contains("SaleRequested", code);
        Assert.Contains("SetActiveSection", code);
    }

    [Fact]
    public void CustomersView_ScrollsShowsStatusInlineAndIsBusyGuarded()
    {
        var xaml = Src("CustomersView.xaml");
        var code = Src("CustomersView.xaml.cs");

        Assert.Contains("<UserControl", xaml);
        Assert.Contains("<ScrollViewer", xaml);
        Assert.Contains("x:Name=\"StatusText\"", xaml);
        Assert.Contains("x:Name=\"BusyPanel\"", xaml);
        Assert.Contains("x:Name=\"FormPanel\"", xaml);
        Assert.Contains("x:Name=\"BusyProgressBar\"", xaml);
        Assert.Contains("_busy.RunAsync", code);
        Assert.DoesNotContain("MessageBox", code);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Theory]
    [InlineData("Text=\"Display Name")]
    [InlineData("Content=\"Save\"")]
    [InlineData("Text=\"Tax Id")]
    [InlineData("Content=\"New Customer\"")]
    public void CustomersView_HasSpanishLabels(string english)
    {
        Assert.DoesNotContain(english, Src("CustomersView.xaml"));
        Assert.Contains("Content=\"Guardar\"", Src("CustomersView.xaml"));
    }

    [Fact]
    public void CustomerFormChoices_KeepTheWireValues_WithSpanishLabels()
    {
        Assert.Equal(["Retail", "Wholesale"], CustomerFormChoices.Kinds.Select(c => c.Value));
        Assert.Equal(["Minorista", "Mayorista"], CustomerFormChoices.Kinds.Select(c => c.Label));
        Assert.Equal(["None", "Cuit", "Cuil", "Dni"], CustomerFormChoices.TaxIdTypes.Select(c => c.Value));
        Assert.Equal(
            ["ConsumidorFinal", "ResponsableInscripto", "Monotributo", "Exento", "NoAplica"],
            CustomerFormChoices.TaxConditions.Select(c => c.Value));
    }

    [Fact]
    public void CustomersWindow_IsGone()
    {
        Assert.Throws<FileNotFoundException>(() => Src("CustomersWindow.xaml"));
    }
}
