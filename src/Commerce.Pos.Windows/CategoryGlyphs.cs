namespace Commerce.Pos.Windows;

/// <summary>
/// Maps the category icon keys of the fixed set (catalog-categories spec;
/// `Commerce.Domain.Catalog.CategoryIcons` and the web picker use the same
/// keys) to the glyph the category rail shows. The glyphs are Segoe UI Emoji
/// code points: WPF renders them monochrome, so they follow the palette's
/// foreground brush in every theme, and unlike the Segoe Fluent/MDL2 icon set
/// they include recognisable food, drink and cleaning symbols. An unknown key
/// (a newer cloud than this terminal) falls back to the generic tag.
/// </summary>
public static class CategoryGlyphs
{
    public const string FontFamilyName = "Segoe UI Emoji";

    public const string GenericKey = "generic";

    private static readonly IReadOnlyDictionary<string, string> Glyphs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["meat"] = "\U0001F969",
        ["poultry"] = "\U0001F357",
        ["fish"] = "\U0001F41F",
        ["wine"] = "\U0001F377",
        ["drinks"] = "\U0001F964",
        ["charcoal"] = "\U0001F525",
        ["grocery"] = "\U0001F6D2",
        ["cleaning"] = "\U0001F9FC",
        ["bakery"] = "\U0001F956",
        ["dairy"] = "\U0001F95B",
        ["produce"] = "\U0001F96C",
        [GenericKey] = "\U0001F3F7",
    };

    public static string For(string? iconKey) =>
        iconKey is not null && Glyphs.TryGetValue(iconKey, out var glyph) ? glyph : Glyphs[GenericKey];
}
