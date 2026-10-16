using System.IO;
using System.Windows;

namespace Commerce.Pos.Windows;

public enum DesktopTheme
{
    Dark,
    Light,
    VacaVerde
}

public sealed record DesktopThemeOption(DesktopTheme Theme, string Label, string ResourceName)
{
    public override string ToString() => Label;
}

/// <summary>
/// One step of a theme switch on the application's merged dictionaries: load <paramref name="Source"/> fresh and put it
/// at <paramref name="Index"/>, replacing the dictionary there or inserting it.
/// </summary>
public sealed record ThemeDictionaryChange(int Index, bool Replaces, string Source);

public static class DesktopThemeService
{
    private const string ThemeFileName = "desktop-theme.txt";
    private const string ThemeBrushesSource = "Themes/ThemeBrushes.xaml";

    public static IReadOnlyList<DesktopThemeOption> ThemeOptions { get; } =
    [
        new(DesktopTheme.Dark, "Oscuro", "DarkTheme"),
        new(DesktopTheme.Light, "Claro", "LightTheme"),
        new(DesktopTheme.VacaVerde, "Vaca Verde", "VacaVerdeTheme")
    ];

    private static string ThemeFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Incoders",
        "Commerce",
        ThemeFileName);

    public static DesktopTheme LoadSavedTheme()
    {
        try
        {
            if (!File.Exists(ThemeFilePath))
            {
                return DesktopTheme.Dark;
            }

            var raw = File.ReadAllText(ThemeFilePath).Trim();
            return Enum.TryParse<DesktopTheme>(raw, ignoreCase: true, out var theme)
                ? theme
                : DesktopTheme.Dark;
        }
        catch
        {
            return DesktopTheme.Dark;
        }
    }

    /// <summary>
    /// The dictionaries a switch to <paramref name="theme"/> loads, in order: the palette, then the semantic brushes
    /// drawn from it (Themes/ThemeBrushes.xaml). The brushes are rebuilt even when they are already merged: a brush
    /// resolves its palette color once, so only a fresh one follows the new palette. Each replaces its own dictionary;
    /// a missing palette goes first and missing brushes right after the palette.
    /// </summary>
    public static IReadOnlyList<ThemeDictionaryChange> PlanApply(IReadOnlyList<string> mergedSources, DesktopTheme theme)
    {
        var palette = $"Themes/{GetThemeOption(theme).ResourceName}.xaml";
        var paletteIndex = IndexOf(mergedSources, IsThemePaletteSource);
        var brushesIndex = IndexOf(mergedSources, IsThemeBrushesSource);

        var paletteChange = paletteIndex >= 0
            ? new ThemeDictionaryChange(paletteIndex, Replaces: true, palette)
            : new ThemeDictionaryChange(0, Replaces: false, palette);

        // Inserting the palette at the top moves every merged dictionary one place down.
        var brushesChange = brushesIndex >= 0
            ? new ThemeDictionaryChange(paletteIndex >= 0 ? brushesIndex : brushesIndex + 1, Replaces: true, ThemeBrushesSource)
            : new ThemeDictionaryChange(paletteChange.Index + 1, Replaces: false, ThemeBrushesSource);

        return [paletteChange, brushesChange];
    }

    public static void SaveTheme(DesktopTheme theme) => Save(theme);

    public static void SaveAndApplyTheme(DesktopTheme theme)
    {
        Save(theme);
        Apply(theme);
    }

    public static void ApplySavedTheme() => Apply(LoadSavedTheme());

    public static string GetDisplayName(DesktopTheme theme) =>
        GetThemeOption(theme).Label;

    public static string GetAppliedMessage(DesktopTheme theme) =>
        $"Tema {GetDisplayName(theme).ToLowerInvariant()} aplicado y guardado para esta terminal.";

    private static void Save(DesktopTheme theme)
    {
        var directory = Path.GetDirectoryName(ThemeFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(ThemeFilePath, theme.ToString());
    }

    private static DesktopThemeOption GetThemeOption(DesktopTheme theme) =>
        ThemeOptions.FirstOrDefault(option => option.Theme == theme) ?? ThemeOptions[0];

    /// <summary>
    /// Swaps the palette and rebuilds the semantic brushes (<see cref="PlanApply"/>). Every view reads theme brushes
    /// through DynamicResource, so replacing the merged dictionaries repaints all open windows at once.
    /// </summary>
    private static void Apply(DesktopTheme theme)
    {
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var sources = resources.Select(dictionary => dictionary.Source?.OriginalString ?? string.Empty).ToList();

        foreach (var change in PlanApply(sources, theme))
        {
            var dictionary = new ResourceDictionary { Source = new Uri(change.Source, UriKind.Relative) };
            if (change.Replaces)
            {
                resources[change.Index] = dictionary;
            }
            else
            {
                resources.Insert(change.Index, dictionary);
            }
        }
    }

    private static int IndexOf(IReadOnlyList<string> sources, Func<string, bool> matches)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (matches(sources[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsThemePaletteSource(string source) =>
        ThemeOptions.Any(option => source.EndsWith($"{option.ResourceName}.xaml", StringComparison.OrdinalIgnoreCase));

    private static bool IsThemeBrushesSource(string source) =>
        source.EndsWith("ThemeBrushes.xaml", StringComparison.OrdinalIgnoreCase);
}
