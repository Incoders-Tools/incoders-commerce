using Commerce.Domain.Catalog;
using Commerce.Domain.Purchasing;

namespace Commerce.Domain.Stock;

/// <summary>
/// Why stock moved. SIGN CONVENTION (quantity is SIGNED, in the presentation's unit): receipts and openings add (+),
/// sales and shrinkage subtract (-), a count correction or adjustment is either, a reversal is the opposite of the
/// movement it reverses. The on-hand balance is never stored: it is SUM(quantity) over the branch's movements.
/// </summary>
public enum StockMovementKind
{
    Opening,
    PurchaseReceipt,
    Sale,
    Adjustment,
    Shrinkage,
    CountCorrection,
    Reversal,
}

public static class StockRules
{
    /// <summary>The kinds an operator may register by hand through POST /stock/adjustments.</summary>
    public static readonly IReadOnlyList<StockMovementKind> ManualKinds =
        [StockMovementKind.Opening, StockMovementKind.Shrinkage, StockMovementKind.CountCorrection, StockMovementKind.Adjustment];

    /// <summary>Mirrors `stock_movements_sign_ck` (0033): the sign each kind must carry.</summary>
    public static bool IsSignValid(StockMovementKind kind, decimal quantity) => kind switch
    {
        StockMovementKind.Opening or StockMovementKind.PurchaseReceipt => quantity > 0,
        StockMovementKind.Sale or StockMovementKind.Shrinkage => quantity < 0,
        _ => quantity != 0,
    };

    public static bool TryValidateAdjustment(
        StockMovementKind kind, QuantityBehavior behavior, decimal quantity, out string? error)
    {
        error = null;
        if (!ManualKinds.Contains(kind))
        {
            error = "kind must be one of: Opening, Shrinkage, CountCorrection, Adjustment.";
            return false;
        }

        if (!IsSignValid(kind, quantity))
        {
            error = kind switch
            {
                StockMovementKind.Opening => "quantity of an Opening must be greater than zero.",
                StockMovementKind.Shrinkage => "quantity of a Shrinkage must be less than zero.",
                _ => "quantity cannot be zero.",
            };
            return false;
        }

        if (!ReceptionRules.TryValidateQuantity(behavior, Math.Abs(quantity), out var quantityError))
        {
            error = quantityError;
            return false;
        }

        return true;
    }

    /// <summary>Below minimum: a minimum is set and the on-hand is strictly under it (negative stock counts, even with a 0 minimum).</summary>
    public static bool IsBelowMinimum(decimal onHand, decimal? minimum) => minimum is { } min && onHand < min;
}
