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

    private readonly QuantityFormat _quantityFormat;

    /// <summary>A card for <paramref name="item"/>; quantities show with <paramref name="quantityFormat"/> (null = the terminal culture).</summary>
    public ProductCardViewModel(CatalogPriceReplicaItem item, QuantityFormat? quantityFormat = null)
    {
        Item = item;
        _quantityFormat = quantityFormat ?? QuantityFormat.Terminal;
    }

    public CatalogPriceReplicaItem Item { get; }

    public Guid PresentationId => Item.PresentationId;

    public string Name => Item.ProductName;

    public string PresentationName => Item.PresentationName;

    public string? Code => Item.IdentificationCode;

    public bool HasPrice => Item.UnitPrice is not null;

    public string Initials => BuildInitials(Item.ProductName);

    public decimal Quantity => _line?.Quantity ?? 0m;

    public bool IsInSale => _line is not null;

    public string QuantityText => SaleQuantity.Text(Quantity, Item.QuantityBehavior, _quantityFormat);

    public string UnitPriceText => _line?.UnitPriceText
        ?? (Item.UnitPrice is { } price ? SaleQuantity.UnitPriceText(price, Item.QuantityBehavior, CultureInfo.CurrentCulture) : "Sin precio");

    /// <summary>The product's total in the sale; empty while it is not in it (a bold "$ 0,00" read as a zero price).</summary>
    public string LineTotalText => _line is { } line ? line.LineTotal.ToString("C", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Why a product cannot be sold to this buyer, for the "Sin precio" tooltip; null when it has a price.</summary>
    public string? NoPriceExplanation => HasPrice
        ? null
        : "Este producto no tiene precio en la lista de este comprador (para Consumidor final, la lista por defecto de la sucursal). " +
          "Cargalo en Listas de precios en la web y sincronizá la terminal.";

    public event PropertyChangedEventHandler? PropertyChanged;

    // --- Stock replica (purchases-receptions-and-stock T5): informative, never blocking. ---

    private StockSnapshot? _stock;

    public bool HasStock => _stock is not null;

    public string StockText => _stock is { } stock
        ? $"Stock: {StockAvailability.QuantityText(stock.OnHand, Item.QuantityBehavior, _quantityFormat)}"
        : string.Empty;

    public string StockAsOfText => _stock is { } stock ? $"al {StockAvailability.AsOfText(stock.AsOfUtc)}" : string.Empty;

    /// <summary>The sale line is over the known stock. The line stays; this only labels the card.</summary>
    public bool HasStockWarning => StockWarningText is not null;

    public string? StockWarningText => IsInSale
        ? StockAvailability.Warning(_stock, Quantity, Item.QuantityBehavior, Item.ProductName, _quantityFormat)
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

    /// <summary>
    /// The rail's default before the terminal has received the categories' POS setting (a server that predates it): these
    /// are not offered, by name (case and accents ignored). Once the setting arrives ("Mostrar en el POS" of each category,
    /// managed on the web), it decides alone. Either way it is only the rail: the products are still found by scanning or
    /// searching and listed under "Todos".
    /// </summary>
    public static IReadOnlyList<string> HiddenCategoryNames { get; } = ["Embutidos", "Achuras"];

    /// <summary>
    /// "Todos" first, then one entry per category in the order given. <paramref name="configured"/>: the categories were
    /// already chosen by their POS setting; otherwise the default hides <see cref="HiddenCategoryNames"/>.
    /// </summary>
    public static IReadOnlyList<CategoryRailItem> Build(IReadOnlyList<CatalogCategory> categories, bool configured = false) =>
    [
        All,
        .. categories
            .Where(c => configured || !IsHidden(c.Name))
            .Select(c => new CategoryRailItem(c.Id.ToString(), c.Name, CategoryGlyphs.For(c.IconKey))),
    ];

    private static bool IsHidden(string name) =>
        HiddenCategoryNames.Any(hidden => BranchSyncStore.FoldText(hidden) == BranchSyncStore.FoldText(name.Trim()));
}
