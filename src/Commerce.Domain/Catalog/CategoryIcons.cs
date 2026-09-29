namespace Commerce.Domain.Catalog;

/// <summary>
/// The fixed vocabulary of category icon keys (catalog-categories spec). The
/// database CHECK on `categories.icon_key` (migration 0018), the web icon
/// picker and the POS glyph map all use exactly these keys; the POS and the
/// web fall back to <see cref="Generic"/> for a key they do not know.
/// </summary>
public static class CategoryIcons
{
    public const string Generic = "generic";

    public static readonly IReadOnlyList<string> Keys =
    [
        "meat", "poultry", "fish", "wine", "drinks", "charcoal",
        "grocery", "cleaning", "bakery", "dairy", "produce", Generic,
    ];

    public static bool IsValid(string? iconKey) => iconKey is not null && Keys.Contains(iconKey);
}
