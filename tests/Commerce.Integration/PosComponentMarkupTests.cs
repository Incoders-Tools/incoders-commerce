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
