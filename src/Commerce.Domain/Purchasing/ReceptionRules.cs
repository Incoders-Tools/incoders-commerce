using Commerce.Domain.Catalog;

namespace Commerce.Domain.Purchasing;

public enum ReceptionStatus
{
    Draft,
    Confirmed,
    Voided,
}

/// <summary>The supplier document that backs a reception (it is what the duplicate guard compares).</summary>
public enum ReceptionDocumentType
{
    Invoice,
    DeliveryNote,
    Other,
}

/// <summary>
/// Pure rules of a goods reception (PRD 9.6 / 9.8): line totals and rounding, the precision of received quantities per
/// <see cref="QuantityBehavior"/> and of unit costs. A line total is ROUNDED PER LINE (half away from zero, 2 decimals)
/// and the reception total is the sum of the rounded lines, so the figure printed per line always adds up to the total
/// that is posted to the supplier account.
/// </summary>
public static class ReceptionRules
{
    public const int MaxQuantityDecimals = 3;
    public const int MaxUnitCostDecimals = 4;
    public const decimal MaxQuantity = 999_999_999_999m;
    public const decimal MaxUnitCost = 999_999_999_999m;

    public static decimal LineTotal(decimal quantity, decimal unitCost) =>
        Math.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);

    public static decimal Total(IEnumerable<(decimal Quantity, decimal UnitCost)> lines) =>
        lines.Sum(l => LineTotal(l.Quantity, l.UnitCost));

    /// <summary>FixedQuantity lines count whole units; Weighted and Bulk lines carry up to three decimals (kg, litres).</summary>
    public static bool TryValidateQuantity(QuantityBehavior behavior, decimal quantity, out string? error)
    {
        error = null;
        if (quantity <= 0 || quantity > MaxQuantity)
        {
            error = "quantity must be greater than zero.";
            return false;
        }

        var decimals = behavior == QuantityBehavior.FixedQuantity ? 0 : MaxQuantityDecimals;
        if (decimal.Round(quantity, decimals) != quantity)
        {
            error = decimals == 0
                ? "quantity must be a whole number for a fixed-quantity presentation."
                : $"quantity allows at most {MaxQuantityDecimals} decimals.";
            return false;
        }

        return true;
    }

    public static bool TryValidateUnitCost(decimal unitCost, out string? error)
    {
        error = null;
        if (unitCost < 0 || unitCost > MaxUnitCost || decimal.Round(unitCost, MaxUnitCostDecimals) != unitCost)
        {
            error = $"unitCost must be zero or more, with at most {MaxUnitCostDecimals} decimals.";
            return false;
        }

        return true;
    }
}
