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

    /// <summary>
    /// The quantity with its unit: kilos for weighted presentations, bare units otherwise; with the organization's
    /// decimal separator (<paramref name="format"/>, null = the terminal culture).
    /// </summary>
    public static string QuantityText(decimal quantity, string quantityBehavior, QuantityFormat? format = null)
    {
        var number = quantity.ToString("0.###", (format ?? QuantityFormat.Terminal).Numbers);
        return quantityBehavior == "Weighted" ? $"{number} kg" : number;
    }

    /// <summary>Null when the requested quantity fits the known stock, or when the stock is unknown.</summary>
    public static string? Warning(
        StockSnapshot? snapshot, decimal requested, string quantityBehavior, string productName, QuantityFormat? format = null) =>
        snapshot is null || requested <= snapshot.OnHand
            ? null
            : $"Stock conocido de {productName}: {QuantityText(snapshot.OnHand, quantityBehavior, format)} (al {AsOfText(snapshot.AsOfUtc)}). " +
              "La cantidad de la venta lo supera; la venta no se bloquea.";

    /// <summary>
    /// One warning line per sale line over its known stock; null when none is. The quantity behavior, used only to label
    /// kilos, is the line's own; <paramref name="behaviorOf"/> supplies it for a line that does not carry one (an unknown
    /// behavior just omits the unit).
    /// </summary>
    public static string? CartWarnings(
        IEnumerable<ScannedSaleLineViewModel> lines, Func<Guid, StockSnapshot?> stockOf, Func<Guid, string>? behaviorOf = null)
    {
        var warnings = lines
            .Select(line => Warning(stockOf(line.PresentationId), line.Quantity, BehaviorOf(line, behaviorOf), line.ProductName, line.QuantityFormat))
            .Where(text => text is not null)
            .ToList();
        return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
    }

    private static string BehaviorOf(ScannedSaleLineViewModel line, Func<Guid, string>? behaviorOf) =>
        line.QuantityBehavior is { Length: > 0 } own ? own : behaviorOf?.Invoke(line.PresentationId) ?? string.Empty;
}
