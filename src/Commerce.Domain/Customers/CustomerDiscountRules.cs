namespace Commerce.Domain.Customers;

/// <summary>
/// A customer's own discount, applied to its prices after the price list (owner-requests-quick-adjustments Q3): a
/// percentage from 0 to 100 with up to two decimals, or none. The server rule both clients follow; it exists because
/// the POS once read "7,5" as 75 and nothing stopped it.
/// </summary>
public static class CustomerDiscountRules
{
    public const decimal Max = 100m;

    public static bool IsValid(decimal? discountPercentage) =>
        discountPercentage is not { } value
        || value is >= 0m and <= Max && decimal.Round(value, 2) == value;
}
