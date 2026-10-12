using Commerce.Domain.Pricing;

namespace Commerce.Domain.Payroll;

/// <summary>
/// The goods an employee takes (employee-lifecycle E4), item by item, from the web (registered like a sale, priced on the
/// default list) or from the POS (a sale on current account to the employee's linked customer). The payroll deducts them
/// with a discount per item and on the total, decided before paying.
/// </summary>
public static class EmployeeGoodsRules
{
    public const int MaxLines = 50;
    public const decimal MaxQuantity = 9_999m;

    /// <summary>A quantity taken: above zero, up to three decimals (kilos), as the stock keeps it.</summary>
    public static bool IsValidQuantity(decimal quantity) => quantity is > 0m and <= MaxQuantity && decimal.Round(quantity, 3) == quantity;

    /// <summary>What an item's discount forgives: its percent of the item's total, to the cent.</summary>
    public static decimal ItemDiscount(decimal lineTotal, decimal discountPercent) => Money.Round2(lineTotal * discountPercent / 100m);

    /// <summary>
    /// Spreads what a sale charged over its lines in proportion to their totals (a sale discount lowers every line alike),
    /// rounded to cents with the last line taking the remainder, so the items always add up to <paramref name="charged"/>.
    /// </summary>
    public static IReadOnlyList<decimal> Spread(IReadOnlyList<decimal> lineTotals, decimal charged)
    {
        var sum = lineTotals.Sum();
        if (sum == 0m || sum == charged)
        {
            return [.. lineTotals];
        }

        var spread = new List<decimal>(lineTotals.Count);
        var assigned = 0m;
        for (var i = 0; i < lineTotals.Count; i++)
        {
            var share = i == lineTotals.Count - 1 ? charged - assigned : Money.Round2(lineTotals[i] * charged / sum);
            spread.Add(share);
            assigned += share;
        }

        return spread;
    }
}
