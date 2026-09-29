using System.Text.RegularExpressions;

namespace Commerce.Integration;

/// <summary>
/// Structural guards for the POS component layer (WPF cannot be instantiated in
/// this test host): the sale surface must stay theme-able, so palette brushes are
/// only ever consumed through DynamicResource, and every component named by the
/// redesign exists.
/// </summary>
public sealed class PosComponentMarkupTests
{
    private static readonly string[] Components = ["PosNavBar", "ProductCard", "SaleLinesTable", "CategoryRail", "TotalsPanel"];

    private static string PosDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Commerce.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Commerce.Pos.Windows");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void EveryRedesignComponent_HasMarkupAndCodeBehind()
    {
        foreach (var name in Components)
        {
            Assert.True(File.Exists(Path.Combine(PosDirectory(), "Controls", name + ".xaml")), name + ".xaml");
            Assert.True(File.Exists(Path.Combine(PosDirectory(), "Controls", name + ".xaml.cs")), name + ".xaml.cs");
        }
    }

    [Fact]
    public void ComponentsAndMainWindow_ConsumePaletteBrushesOnlyThroughDynamicResource()
    {
        var files = Components.Select(c => Path.Combine(PosDirectory(), "Controls", c + ".xaml"))
            .Append(Path.Combine(PosDirectory(), "MainWindow.xaml"));

        foreach (var file in files)
        {
            var staticBrushes = Regex.Matches(File.ReadAllText(file), @"\{StaticResource\s+\w*Brush\w*\}");
            Assert.True(staticBrushes.Count == 0, $"{Path.GetFileName(file)} uses StaticResource for a brush: {string.Join(", ", staticBrushes.Select(m => m.Value))}");
        }
    }

    [Fact]
    public void DiscountWindow_ExistsAndIsThemedThroughDynamicResourceOnly()
    {
        var xaml = File.ReadAllText(Path.Combine(PosDirectory(), "DiscountWindow.xaml"));

        Assert.True(File.Exists(Path.Combine(PosDirectory(), "DiscountWindow.xaml.cs")));
        Assert.Contains("PinBox", xaml);
        Assert.Contains("PercentTextBox", xaml);
        Assert.Contains("ApplyButton", xaml);
        Assert.Contains("RemoveDiscountButton", xaml);
        Assert.Contains("<PasswordBox", xaml);
        Assert.Contains("{DynamicResource ShellBrush}", xaml);
        Assert.Empty(Regex.Matches(xaml, @"\{StaticResource\s+\w*Brush\w*\}"));
    }

    [Fact]
    public void SaleTable_OffersAPerLineDiscountAction_AndShowsTheDiscountedTotal()
    {
        var xaml = File.ReadAllText(Path.Combine(PosDirectory(), "Controls", "SaleLinesTable.xaml"));
        var code = File.ReadAllText(Path.Combine(PosDirectory(), "Controls", "SaleLinesTable.xaml.cs"));

        Assert.Contains("Aplicar descuento", xaml);
        Assert.Contains("DiscountText", xaml);
        Assert.Contains("NetTotalText", xaml);
        Assert.Contains("LineDiscountRequested", code);
    }

    [Fact]
    public void TotalsPanel_HasADescuentoRowAndASaleDiscountAction_AndKeepsTheOldNames()
    {
        var xaml = File.ReadAllText(Path.Combine(PosDirectory(), "Controls", "TotalsPanel.xaml"));
        var code = File.ReadAllText(Path.Combine(PosDirectory(), "Controls", "TotalsPanel.xaml.cs"));

        Assert.Contains("Descuento", xaml);
        Assert.Contains("x:Name=\"DiscountRow\"", xaml);
        Assert.Contains("x:Name=\"SaleDiscountButton\"", xaml);
        Assert.Contains("SaleDiscountRequested", code);
        Assert.Contains("x:Name=\"ScannedTotalText\"", xaml);
        Assert.Contains("x:Name=\"CommitScannedSaleButton\"", xaml);
    }

    [Fact]
    public void MainWindow_WiresTheDiscountActions_AndTheSaleCommitNeverReadsTheDeviceToken()
    {
        var xaml = File.ReadAllText(Path.Combine(PosDirectory(), "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(PosDirectory(), "MainWindow.xaml.cs"));

        Assert.Contains("LineDiscountRequested=", xaml);
        Assert.Contains("SaleDiscountRequested=", xaml);

        // CompleteScannedSale is committed with the cart discounts and authorization, offline.
        var commit = code[code.IndexOf("private void CommitScannedSaleButton_Click", StringComparison.Ordinal)..];
        commit = commit[..commit.IndexOf("Task 7.7", StringComparison.Ordinal)];
        Assert.Contains("saleDiscount: _cart.SaleDiscount", commit);
        Assert.Contains("discountAuthorization: _cart.Authorization", commit);
        Assert.DoesNotContain("DeviceToken", commit);
    }

    [Fact]
    public void TenderButtons_AreDisabledWithAComingSoonTooltip()
    {
        var totals = File.ReadAllText(Path.Combine(PosDirectory(), "Controls", "TotalsPanel.xaml"));

        Assert.Contains("Efectivo", totals);
        Assert.Contains("Tarjeta", totals);
        Assert.Contains("QR", totals);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", totals);
        Assert.Contains("Próximamente", totals);
    }
}
