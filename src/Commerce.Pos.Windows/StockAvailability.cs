using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>The on-hand the terminal last learned from the cloud and when that replica was refreshed.</summary>
public sealed record StockSnapshot(decimal OnHand, DateTimeOffset AsOfUtc);

/// <summary>
/// Operator-facing (Spanish) display logic of the stock replica (purchases-receptions-and-stock T5). Pure: no WPF, no
/// I/O. POLICY v1: a sale over the known stock WARNS and never blocks (the replica can be stale offline). A presentation
/// without a replica row is UNKNOWN, not zero, and never warns.
/// </summary>
public static class StockAvailability
{
    /// <summary>"dd/MM HH:mm" in the terminal's local time.</summary>
    public static string AsOfText(DateTimeOffset asOfUtc) =>
        asOfUtc.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The quantity with its unit: kilos for weighted presentations, bare units otherwise.</summary>
    public static string QuantityText(decimal quantity, string quantityBehavior)
    {
        var number = quantity.ToString("0.###", CultureInfo.CurrentCulture);
        return quantityBehavior == "Weighted" ? $"{number} kg" : number;
    }

    /// <summary>Null when the requested quantity fits the known stock, or when the stock is unknown.</summary>
    public static string? Warning(StockSnapshot? snapshot, decimal requested, string quantityBehavior, string productName) =>
        snapshot is null || requested <= snapshot.OnHand
            ? null
            : $"Stock conocido de {productName}: {QuantityText(snapshot.OnHand, quantityBehavior)} (al {AsOfText(snapshot.AsOfUtc)}). " +
              "La cantidad de la venta lo supera; la venta no se bloquea.";

    /// <summary>
    /// One warning line per sale line over its known stock; null when none is. <paramref name="behaviorOf"/> supplies the
    /// quantity behavior used only to label kilos (an unknown behavior just omits the unit).
    /// </summary>
    public static string? CartWarnings(
        IEnumerable<ScannedSaleLineViewModel> lines, Func<Guid, StockSnapshot?> stockOf, Func<Guid, string>? behaviorOf = null)
    {
        var warnings = lines
            .Select(line => Warning(stockOf(line.PresentationId), line.Quantity, behaviorOf?.Invoke(line.PresentationId) ?? string.Empty, line.ProductName))
            .Where(text => text is not null)
            .ToList();
        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }
}
