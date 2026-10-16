using System.Text.RegularExpressions;
using System.Xml.Linq;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Live theme switching (price-editing-and-desktop-polish T2): choosing Oscuro / Claro / Vaca Verde in Settings repaints
/// every open window at once. That needs (1) every palette color and theme brush consumed through DynamicResource,
/// never StaticResource (resolved once at load); (2) the semantic brushes rebuilt on a switch, because a brush that
/// lives in an application dictionary resolves its palette color once and never follows a later palette; and (3) no
/// code-behind holding a brush it read once. WPF cannot be instantiated in this test host, so (1) and (3) scan the
/// markup and code and (2) checks the dictionaries the theme service swaps.
/// </summary>
public sealed class PosLiveThemeTests
{
    private static readonly string[] Palettes = ["DarkTheme", "LightTheme", "VacaVerdeTheme"];

    private static string PosDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Commerce.Pos.Windows");
    }

    private static string Src(params string[] path) => File.ReadAllText(Path.Combine([PosDir(), .. path]));

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.GetFiles(PosDir(), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string? Key(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

    /// <summary>The palette's color keys (read from the palette files) and every brush key declared under Themes/.</summary>
    private static HashSet<string> ThemeKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var palette in Palettes)
        {
            foreach (var color in XDocument.Parse(Src("Themes", palette + ".xaml")).Root!.Elements())
            {
                keys.Add(Key(color)!);
            }
        }

        foreach (var file in Directory.GetFiles(Path.Combine(PosDir(), "Themes"), "*.xaml"))
        {
            foreach (var brush in XDocument.Parse(File.ReadAllText(file)).Descendants()
                         .Where(e => e.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal) && Key(e) is not null))
            {
                keys.Add(Key(brush)!);
            }
        }

        return keys;
    }

    [Fact]
    public void ThemeKeys_AreReadFromThePaletteFiles()
    {
        var keys = ThemeKeys();

        Assert.Contains("ColorSurface", keys);
        Assert.Contains("ColorText", keys);
        Assert.Contains("SurfaceBrush", keys);
        Assert.Contains("TextBrush", keys);
        Assert.Contains("OnPrimaryBrush", keys);
    }

    [Fact]
    public void NoXaml_ConsumesAPaletteColorOrThemeBrush_ThroughStaticResource()
    {
        var keys = ThemeKeys();
        var offenders = new List<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            var text = File.ReadAllText(file);
            // {StaticResource Key}, {StaticResource ResourceKey=Key} and the element form <StaticResource ResourceKey="Key" />.
            foreach (Match use in Regex.Matches(text, @"\{StaticResource\s+(?:ResourceKey=)?(\w+)\s*\}|<StaticResource\s+ResourceKey=""(\w+)"""))
            {
                var key = use.Groups[1].Success ? use.Groups[1].Value : use.Groups[2].Value;
                if (keys.Contains(key))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {use.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Theme keys must be DynamicResource:\n" + string.Join("\n", offenders));
    }

    // ---- the semantic brushes follow the palette ---------------------------------------------

    [Fact]
    public void SemanticBrushes_LiveInTheirOwnDictionary_DrawnFromThePalette()
    {
        var brushes = XDocument.Parse(Src("Themes", "ThemeBrushes.xaml")).Root!.Elements().ToList();
        var paletteKeys = XDocument.Parse(Src("Themes", "DarkTheme.xaml")).Root!.Elements().Select(e => Key(e)!).ToHashSet();

        Assert.NotEmpty(brushes);
        Assert.All(brushes, brush =>
        {
            Assert.Equal("SolidColorBrush", brush.Name.LocalName);
            var color = Regex.Match(brush.Attribute("Color")!.Value, @"^\{DynamicResource (\w+)\}$");
            Assert.True(color.Success, $"{Key(brush)} must take its color from the palette");
            Assert.Contains(color.Groups[1].Value, paletteKeys);
        });

        // A brush left in DesktopTheme.xaml would keep the first palette's color: the service never rebuilds it.
        Assert.DoesNotContain(XDocument.Parse(Src("Themes", "DesktopTheme.xaml")).Descendants(),
            e => e.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal) && Key(e) is not null);
    }

    [Fact]
    public void App_MergesThePalette_ThenTheBrushes_ThenTheStyles()
    {
        var sources = XDocument.Parse(Src("App.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute("Source") is not null)
            .Select(e => e.Attribute("Source")!.Value)
            .ToList();

        Assert.Equal(["Themes/DarkTheme.xaml", "Themes/ThemeBrushes.xaml", "Themes/DesktopTheme.xaml", "Controls/EntityListView.xaml"], sources);
    }

    private static readonly string[] AppSources =
        ["Themes/DarkTheme.xaml", "Themes/ThemeBrushes.xaml", "Themes/DesktopTheme.xaml", "Controls/EntityListView.xaml"];

    [Theory]
    [InlineData(DesktopTheme.Light, "Themes/LightTheme.xaml")]
    [InlineData(DesktopTheme.VacaVerde, "Themes/VacaVerdeTheme.xaml")]
    [InlineData(DesktopTheme.Dark, "Themes/DarkTheme.xaml")]
    public void Apply_ReplacesThePalette_AndRebuildsTheBrushes_InPlace(DesktopTheme theme, string palette)
    {
        var plan = DesktopThemeService.PlanApply(AppSources, theme);

        Assert.Equal(
            [new ThemeDictionaryChange(0, Replaces: true, palette), new ThemeDictionaryChange(1, Replaces: true, "Themes/ThemeBrushes.xaml")],
            plan);
    }

    [Fact]
    public void Apply_RecognizesTheDictionariesItLoaded_ByTheirFullSource()
    {
        // After a switch the merged dictionaries carry the sources the service gave them.
        var afterSwitch = AppSources.Select(s => s == "Themes/DarkTheme.xaml" ? "Themes/VacaVerdeTheme.xaml" : s).ToList();

        var plan = DesktopThemeService.PlanApply(afterSwitch, DesktopTheme.Light);

        Assert.Equal(new ThemeDictionaryChange(0, Replaces: true, "Themes/LightTheme.xaml"), plan[0]);
        Assert.Equal(new ThemeDictionaryChange(1, Replaces: true, "Themes/ThemeBrushes.xaml"), plan[1]);
    }

    [Fact]
    public void Apply_InsertsWhatIsMissing_PaletteFirst_BrushesRightAfterIt()
    {
        Assert.Equal(
            [new ThemeDictionaryChange(0, Replaces: false, "Themes/LightTheme.xaml"), new ThemeDictionaryChange(1, Replaces: false, "Themes/ThemeBrushes.xaml")],
            DesktopThemeService.PlanApply(["Themes/DesktopTheme.xaml"], DesktopTheme.Light));

        // Inserting the palette at 0 moves the brushes one place down.
        Assert.Equal(
            [new ThemeDictionaryChange(0, Replaces: false, "Themes/LightTheme.xaml"), new ThemeDictionaryChange(2, Replaces: true, "Themes/ThemeBrushes.xaml")],
            DesktopThemeService.PlanApply(["Themes/DesktopTheme.xaml", "Themes/ThemeBrushes.xaml"], DesktopTheme.Light));
    }

    [Fact]
    public void Apply_ExecutesThePlan_WithFreshDictionaries()
    {
        var code = Src("DesktopThemeService.cs");
        var apply = Regex.Match(code, @"private static void Apply\(DesktopTheme theme\)[\s\S]*?\n    }");

        Assert.True(apply.Success);
        Assert.Contains("PlanApply(", apply.Value);
        Assert.Contains("new ResourceDictionary { Source = ", apply.Value);
    }

    // ---- Settings applies at once; code-behind follows the theme --------------------------------

    [Fact]
    public void Settings_AppliesTheChosenThemeImmediately()
    {
        var code = Src("SettingsWindow.xaml.cs");
        var handler = Regex.Match(code, @"private void ThemeRadioButton_Checked\([\s\S]*?\n    }");

        Assert.True(handler.Success);
        Assert.Contains("DesktopThemeService.SaveAndApplyTheme(theme)", handler.Value);
        Assert.Matches(@"public static void SaveAndApplyTheme\(DesktopTheme theme\)\s*\{\s*Save\(theme\);\s*Apply\(theme\);", Src("DesktopThemeService.cs"));
    }

    [Fact]
    public void NoCodeBehind_KeepsABrushItReadOnce()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match read in Regex.Matches(File.ReadAllText(file), @"\((?:System\.Windows\.Media\.)?\w*Brush\)\s*(?:Try)?FindResource\(|(?:Try)?FindResource\([^)]*Brush""\)"))
            {
                offenders.Add($"{Path.GetFileName(file)}: {read.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "Use SetResourceReference so the brush follows a theme switch:\n" + string.Join("\n", offenders));
    }
}
