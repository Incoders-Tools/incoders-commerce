using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>
/// The quantity rules of a sale line per quantity behavior of its presentation (operator-ux-adjustments T3). Pure: no
/// WPF. Weighted and Bulk presentations are MEASURED (kilos, litres): more than zero with up to three decimals, the same
/// precision a reception accepts (<see cref="Commerce.Domain.Purchasing.ReceptionRules.MaxQuantityDecimals"/>).
/// Anything else counts whole units.
///
/// Decimal separator: the terminal does not receive the organization's quantity separator (it is not in the pairing nor
/// in any replica), so the operator may type a decimal comma OR a decimal point (Spanish keyboards type the comma) and a
/// quantity is shown with the separator of the terminal culture. A thousands separator is never accepted.
/// </summary>
public static class SaleQuantity
{
    public const string Weighted = "Weighted";
    public const string Bulk = "Bulk";
    public const int MeasuredDecimals = Commerce.Domain.Purchasing.ReceptionRules.MaxQuantityDecimals;

    /// <summary>The line is in kilos (or litres) with decimals and is entered by its measure, never stepped by one.</summary>
    public static bool IsMeasured(string? quantityBehavior) => quantityBehavior is Weighted or Bulk;

    /// <summary>Null when <paramref name="quantity"/> is valid for the behavior; otherwise the operator-facing reason.</summary>
    public static string? Validate(decimal quantity, string? quantityBehavior)
    {
        if (IsMeasured(quantityBehavior))
        {
            if (quantity <= 0m)
            {
                return quantityBehavior == Weighted ? "Los kilos deben ser mayores que 0." : "La cantidad debe ser mayor que 0.";
            }

            return decimal.Round(quantity, MeasuredDecimals) == quantity
                ? null
                : $"La cantidad admite hasta {MeasuredDecimals} decimales.";
        }

        if (quantity <= 0m)
        {
            return "La cantidad debe ser mayor que 0.";
        }

        return decimal.Truncate(quantity) == quantity ? null : "Este producto se vende por unidad: use cantidades enteras.";
    }

    /// <summary>Reads what the operator typed ("0,550" or "0.550"); only a valid quantity for the behavior passes.</summary>
    public static bool TryParse(string? text, string? quantityBehavior, out decimal quantity, out string? error)
    {
        quantity = 0m;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Contains(',') && trimmed.Contains('.'))
        {
            error = InvalidNumberMessage;
            return false;
        }

        if (!decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            error = InvalidNumberMessage;
            return false;
        }

        error = Validate(parsed, quantityBehavior);
        if (error is not null)
        {
            return false;
        }

        quantity = parsed;
        return true;
    }

    /// <summary>The quantity as the sale shows it: "0,550 kg" for weighted, three decimals for bulk, whole units otherwise.</summary>
    public static string Text(decimal quantity, string? quantityBehavior, CultureInfo culture)
    {
        var number = EditText(quantity, quantityBehavior, culture);
        return quantityBehavior == Weighted ? $"{number} kg" : number;
    }

    /// <summary>The bare number, as it is prefilled in an edit box (parses back with <see cref="TryParse"/>).</summary>
    public static string EditText(decimal quantity, string? quantityBehavior, CultureInfo culture) =>
        IsMeasured(quantityBehavior)
            ? quantity.ToString("0.000", culture)
            : quantity.ToString("0.##", culture);

    /// <summary>The unit price: per kilo for weighted presentations.</summary>
    public static string UnitPriceText(decimal unitPrice, string? quantityBehavior, CultureInfo culture)
    {
        var price = unitPrice.ToString("C", culture);
        return quantityBehavior == Weighted ? $"{price}/kg" : price;
    }

    private const string InvalidNumberMessage = "Ingrese un número, por ejemplo 0,550.";
}
