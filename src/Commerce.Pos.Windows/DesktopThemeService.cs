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

public static class DesktopThemeService
{
    private const string ThemeFileName = "desktop-theme.txt";

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

    private static void Apply(DesktopTheme theme)
    {
        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        var paletteSource = new Uri($"Themes/{GetThemeOption(theme).ResourceName}.xaml", UriKind.Relative);
        var palette = new ResourceDictionary { Source = paletteSource };

        for (var i = 0; i < resources.Count; i++)
        {
            var source = resources[i].Source?.OriginalString ?? string.Empty;
            if (IsThemePaletteSource(source))
            {
                resources[i] = palette;
                return;
            }
        }

        resources.Insert(0, palette);
    }

    private static bool IsThemePaletteSource(string source) =>
        ThemeOptions.Any(option => source.EndsWith($"{option.ResourceName}.xaml", StringComparison.OrdinalIgnoreCase));
}
