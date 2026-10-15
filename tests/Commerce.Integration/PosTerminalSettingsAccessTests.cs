using System.Text.RegularExpressions;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Owner feedback 2026-10-15 round 2 (S2): a cashier, or any operator who is not an administrator, does not see the
/// technical settings of the terminal ("Identidad técnica / Reconfigurar terminal", "Buscar actualizaciones"); they keep
/// Apariencia, Sincronización and Sucursal y estado.
/// </summary>
public sealed class PosTerminalSettingsAccessTests
{
    private static CachedOperator Op(int permissions) =>
        new(Guid.NewGuid(), "op@vacaverde.test", Guid.NewGuid(), [1], [2], DateTimeOffset.UtcNow, permissions);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Src(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Commerce.Pos.Windows", file));

    [Fact]
    public void OnlyAnAdministrator_SeesTheTechnicalSections()
    {
        Assert.True(TerminalSettingsAccess.ShowsTechnicalSections(Op((int)(Permission.OperatePos | Permission.ManageUsers))));
        Assert.False(TerminalSettingsAccess.ShowsTechnicalSections(Op((int)Permission.OperatePos)));
        Assert.False(TerminalSettingsAccess.ShowsTechnicalSections(Op((int)(Permission.OperatePos | Permission.ViewSales | Permission.ManageCatalog))));
        Assert.False(TerminalSettingsAccess.ShowsTechnicalSections(null));
    }

    [Fact]
    public void SettingsWindow_HidesTheTechnicalSections_AndKeepsAppearanceSyncAndStatus()
    {
        var xaml = Src("SettingsWindow.xaml");
        var code = Src("SettingsWindow.xaml.cs");

        var identity = Regex.Match(xaml, @"<Border[^>]*x:Name=""TechnicalIdentitySection""[\s\S]*?</Border>").Value;
        var updates = Regex.Match(xaml, @"<Border[^>]*x:Name=""UpdatesSection""[\s\S]*?</Border>").Value;
        Assert.Contains("Reconfigurar terminal", identity);
        Assert.Contains("Buscar actualizaciones", updates);
        Assert.Contains("Sincronizar pendientes", xaml);
        Assert.Contains("ThemeRadioButton_Checked", xaml);

        Assert.Contains("bool showTechnicalSections", code);
        Assert.Matches(@"TechnicalIdentitySection\.Visibility\s*=\s*showTechnicalSections", code);
        Assert.Matches(@"UpdatesSection\.Visibility\s*=\s*showTechnicalSections", code);
    }

    [Fact]
    public void MainWindow_OpensTheSettings_WithTheSignedInOperatorsAccess()
    {
        var open = Regex.Match(Src("MainWindow.xaml.cs"), @"void RepairButton_Click\([\s\S]*?\n    }").Value;

        Assert.Contains("TerminalSettingsAccess.ShowsTechnicalSections(_currentOperator.Value)", open);
    }
}
