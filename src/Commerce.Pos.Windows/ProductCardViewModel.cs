using System.ComponentModel;
using System.Globalization;
using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// One catalog card. Shows the replicated catalog price until the
/// presentation is in the sale; then it mirrors the cart line (quantity and the
/// price the pricing service actually resolved). It never resolves a price.
/// </summary>
public sealed class ProductCardViewModel : INotifyPropertyChanged
{
    private ScannedSaleLineViewModel? _line;

    public ProductCardViewModel(CatalogPriceReplicaItem item) => Item = item;

    public CatalogPriceReplicaItem Item { get; }

    public Guid PresentationId => Item.PresentationId;

    public string Name => Item.ProductName;

    public string PresentationName => Item.PresentationName;

    public string? Code => Item.IdentificationCode;

    public bool HasPrice => Item.UnitPrice is not null;

    public string Initials => BuildInitials(Item.ProductName);

    public decimal Quantity => _line?.Quantity ?? 0m;

    public bool IsInSale => _line is not null;

    public string QuantityText => Quantity.ToString("0.##", CultureInfo.InvariantCulture);

    public string UnitPriceText => _line?.UnitPriceText
        ?? (Item.UnitPrice is { } price ? price.ToString("C", CultureInfo.CurrentCulture) : "Sin precio");

    public string LineTotalText => (_line?.LineTotal ?? 0m).ToString("C", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    // --- Stock replica (purchases-receptions-and-stock T5): informative, never blocking. ---

    private StockSnapshot? _stock;

    public bool HasStock => _stock is not null;

    public string StockText => _stock is { } stock
        ? $"Stock: {StockAvailability.QuantityText(stock.OnHand, Item.QuantityBehavior)}"
        : string.Empty;

    public string StockAsOfText => _stock is { } stock ? $"al {StockAvailability.AsOfText(stock.AsOfUtc)}" : string.Empty;

    /// <summary>The sale line is over the known stock. The line stays; this only labels the card.</summary>
    public bool HasStockWarning => StockWarningText is not null;

    public string? StockWarningText => IsInSale
        ? StockAvailability.Warning(_stock, Quantity, Item.QuantityBehavior, Item.ProductName)
        : null;

    /// <summary>Applies (or clears, with null) the last known stock of this presentation.</summary>
    public void ApplyStock(StockSnapshot? stock)
    {
        if (Equals(_stock, stock))
        {
            return;
        }

        _stock = stock;
        RaiseStockChanged();
    }

    private void RaiseStockChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(HasStock), nameof(StockText), nameof(StockAsOfText), nameof(HasStockWarning), nameof(StockWarningText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public void ApplyLine(ScannedSaleLineViewModel? line)
    {
        if (Equals(_line, line))
        {
            return;
        }

        _line = line;
        RaiseStockChanged();
        foreach (var name in new[]
                 {
                     nameof(Quantity), nameof(IsInSale), nameof(QuantityText), nameof(UnitPriceText), nameof(LineTotalText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static string BuildInitials(string name)
    {
        var letters = name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(word => char.ToUpper(word[0], CultureInfo.CurrentCulture))
            .ToArray();
        return letters.Length == 0 ? "?" : new string(letters);
    }
}

/// <summary>
/// An entry of the category rail. <see cref="Key"/> is null for the "all
/// products" entry, otherwise the category id. <see cref="GlyphFontFamily"/> is
/// the icon font for "Todos" and the emoji font for real categories (see
/// <see cref="CategoryGlyphs"/>).
/// </summary>
public sealed record CategoryRailItem(
    string? Key, string Name, string Glyph, string GlyphFontFamily = CategoryGlyphs.FontFamilyName)
{
    public static CategoryRailItem All { get; } =
        new(null, "Todos", "", "Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>"Todos" first, then one entry per local category in the order given.</summary>
    public static IReadOnlyList<CategoryRailItem> Build(IReadOnlyList<CatalogCategory> categories) =>
        [All, .. categories.Select(c => new CategoryRailItem(c.Id.ToString(), c.Name, CategoryGlyphs.For(c.IconKey)))];
}
