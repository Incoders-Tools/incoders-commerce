using System.Globalization;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>
/// Parses the percentage the cashier types in the discount prompt. Spanish
/// keyboards type a decimal comma, so both "12,5" and "12.5" are read (with an
/// optional trailing percent sign); a thousands separator is never accepted. Only
/// a valid discount percentage passes: more than 0 and at most 100, two decimals.
/// </summary>
public static class DiscountPercentInput
{
    public static bool TryParse(string? text, out decimal percent)
    {
        percent = 0m;
        var normalized = (text ?? string.Empty).Trim().TrimEnd('%').Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percent)
            && DiscountMath.IsValidPercent(percent);
    }
}
