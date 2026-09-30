using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Commerce.Integration;

/// <summary>
/// Accessibility guard for the POS themes. Every foreground/background pair that a state of the
/// shared templates can put on screen is declared below by palette key, and each theme must give it
/// a WCAG AA contrast ratio (4.5:1 for text, 3:1 for graphical indicators). Palette edits that would
/// make text unreadable (dark text on the green fill, muted text on a hover surface, ...) fail here.
/// Disabled controls are exempt from WCAG; their dimming is an opacity, not a palette pair.
/// </summary>
public sealed class PosThemeContrastTests
{
    private static readonly string[] Themes = ["DarkTheme", "LightTheme", "VacaVerdeTheme"];

    private static readonly string[] Surfaces =
        ["ColorShell", "ColorShellElevated", "ColorSurface", "ColorSurfaceSoft", "ColorInput", "ColorHoverSurface", "ColorPressedSurface"];

    // (foreground key, background key) pairs that are text, so they need 4.5:1.
    private static IEnumerable<(string Fg, string Bg)> TextPairs()
    {
        foreach (var surface in Surfaces)
        {
            yield return ("ColorText", surface);
            yield return ("ColorTextMuted", surface);
        }

        // Filled controls: primary button, selected item and active nav entry, in rest, hover and pressed states.
        foreach (var fill in new[] { "ColorPrimary", "ColorPrimaryHover", "ColorPrimaryPressed" })
        {
            yield return ("ColorOnPrimary", fill);
        }

        // Accent used as text or as an icon glyph (links, discount marks, totals) on every surface it sits on.
        foreach (var surface in new[] { "ColorShell", "ColorShellElevated", "ColorSurface", "ColorSurfaceSoft", "ColorHoverSurface" })
        {
            yield return ("ColorAccentText", surface);
        }

        foreach (var surface in new[] { "ColorShell", "ColorSurface", "ColorSurfaceSoft", "ColorDangerSurface" })
        {
            yield return ("ColorDanger", surface);
        }

        foreach (var surface in new[] { "ColorShell", "ColorSurface", "ColorSurfaceSoft" })
        {
            yield return ("ColorSuccess", surface);
        }

        yield return ("ColorWarningText", "ColorWarningSurface");
    }

    // Non-text indicators (focus and hover borders, selected outlines) need 3:1 against what they sit on.
    private static IEnumerable<(string Fg, string Bg)> IndicatorPairs()
    {
        foreach (var surface in new[] { "ColorShell", "ColorShellElevated", "ColorSurface", "ColorSurfaceSoft", "ColorHoverSurface" })
        {
            yield return ("ColorAccent", surface);
        }

        yield return ("ColorPrimary", "ColorSurface");
        yield return ("ColorScrollThumb", "ColorSurfaceSoft");
    }

    public static IEnumerable<object[]> TextCases() =>
        from theme in Themes from pair in TextPairs() select new object[] { theme, pair.Fg, pair.Bg };

    public static IEnumerable<object[]> IndicatorCases() =>
        from theme in Themes from pair in IndicatorPairs() select new object[] { theme, pair.Fg, pair.Bg };

    [Theory]
    [MemberData(nameof(TextCases))]
    public void TextPair_MeetsWcagAaNormalText(string theme, string foreground, string background) =>
        AssertContrast(theme, foreground, background, 4.5);

    [Theory]
    [MemberData(nameof(IndicatorCases))]
    public void IndicatorPair_MeetsWcagAaNonTextContrast(string theme, string foreground, string background) =>
        AssertContrast(theme, foreground, background, 3.0);

    [Fact]
    public void EveryThemeDeclaresTheSamePaletteKeys()
    {
        var keys = Themes.ToDictionary(t => t, t => Palette(t).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(keys["DarkTheme"], keys["LightTheme"]);
        Assert.Equal(keys["DarkTheme"], keys["VacaVerdeTheme"]);
    }

    [Fact]
    public void ImplicitTextBlockStyle_DoesNotForceAForeground_SoTextInsideControlsFollowTheControl()
    {
        var xaml = XDocument.Parse(Src("Themes", "DesktopTheme.xaml"));
        var implicitStyle = xaml.Descendants().Single(e =>
            e.Name.LocalName == "Style" && Attr(e, "TargetType") == "TextBlock" && Attr(e, "Key") is null);

        var setters = implicitStyle.Elements().Where(e => e.Name.LocalName == "Setter").Select(e => Attr(e, "Property"));
        Assert.DoesNotContain("Foreground", setters);
    }

    [Fact]
    public void EveryWindowSetsItsForeground_BecauseTextInheritsIt()
    {
        var windows = Directory.GetFiles(PosDir(), "*Window.xaml");
        Assert.NotEmpty(windows);
        foreach (var file in windows)
        {
            var root = XDocument.Load(file).Root!;
            Assert.True(Attr(root, "Foreground") is not null, $"{Path.GetFileName(file)} must set Foreground on its root");
        }
    }

    [Fact]
    public void Templates_UseOnPrimaryInsteadOfAHardCodedWhite()
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(PosDir(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")))
        {
            var text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, "(Foreground|Value)=\"(White|Black|#[0-9A-Fa-f]{3,8})\""),
                $"{Path.GetFileName(file)} hard-codes a foreground colour; use an OnPrimary/Text brush");
        }
    }

    private static void AssertContrast(string theme, string foreground, string background, double minimum)
    {
        var palette = Palette(theme);
        Assert.True(palette.ContainsKey(foreground), $"{theme} is missing palette key {foreground}");
        Assert.True(palette.ContainsKey(background), $"{theme} is missing palette key {background}");

        var ratio = Ratio(palette[foreground], palette[background]);
        Assert.True(ratio >= minimum,
            $"{theme}: {foreground} on {background} is {ratio:0.00}:1, below the required {minimum}:1");
    }

    private static Dictionary<string, (double R, double G, double B)> Palette(string theme)
    {
        var doc = XDocument.Parse(Src("Themes", $"{theme}.xaml"));
        return doc.Descendants().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => Attr(e, "Key")!, e => Parse(e.Value.Trim()));
    }

    private static (double R, double G, double B) Parse(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        double Channel(int i) => int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return (Channel(0), Channel(1), Channel(2));
    }

    private static double Luminance((double R, double G, double B) c)
    {
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string PosDir() => Path.Combine(Root(), "src", "Commerce.Pos.Windows");

    private static string Src(params string[] path) => File.ReadAllText(Path.Combine([PosDir(), .. path]));
}
