namespace Commerce.Domain.Catalog;

/// <summary>
/// Optional extension point for vertical-specific catalogue rules (e.g.
/// pharmacy lot/expiry, restaurant modifiers) without coupling the core
/// Product/Presentation contracts to any single vertical.
/// </summary>
public interface IVerticalCatalogExtension
{
    string VerticalName { get; }
    IReadOnlyDictionary<string, string> Attributes { get; }
}

/// <summary>Default no-op extension for products with no vertical-specific data.</summary>
public sealed class NoVerticalCatalogExtension : IVerticalCatalogExtension
{
    public static readonly NoVerticalCatalogExtension Instance = new();

    public string VerticalName => "none";

    public IReadOnlyDictionary<string, string> Attributes { get; } =
        new Dictionary<string, string>();

    private NoVerticalCatalogExtension()
    {
    }
}
