using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>
/// How this terminal shows quantities (operator-ux-adjustments T5): with the organization's quantity decimal separator
/// as last synced (`organizations.quantity_decimal_separator`, <c>Comma</c> or <c>Dot</c>, stored in <c>branch.db</c>),
/// never a thousands separator. A terminal that never synced it (or got a value it does not know) keeps the previous
/// behavior: <see cref="Terminal"/>, the terminal culture's separator. Only quantities follow it; money keeps its format.
/// Typing is not affected: <see cref="SaleQuantity.TryParse"/> accepts a comma or a point either way.
/// </summary>
public sealed class QuantityFormat
{
    public const string Comma = "Comma";
    public const string Dot = "Dot";

    private static readonly QuantityFormat CommaFormat = new(Comma, ",");
    private static readonly QuantityFormat DotFormat = new(Dot, ".");

    private readonly NumberFormatInfo? _numbers;

    private QuantityFormat(string? separator, string? symbol)
    {
        Separator = separator;
        if (symbol is not null)
        {
            var numbers = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
            numbers.NumberDecimalSeparator = symbol;
            numbers.NumberGroupSeparator = string.Empty;
            _numbers = NumberFormatInfo.ReadOnly(numbers);
        }
    }

    /// <summary>Never synced: quantities show with the terminal culture's decimal separator.</summary>
    public static QuantityFormat Terminal { get; } = new(null, null);

    /// <summary>The organization's separator this format applies (<c>Comma</c> / <c>Dot</c>), or null for <see cref="Terminal"/>.</summary>
    public string? Separator { get; }

    /// <summary>The number format quantities are written with (read at call time for <see cref="Terminal"/>).</summary>
    public IFormatProvider Numbers => (IFormatProvider?)_numbers ?? CultureInfo.CurrentCulture;

    public static bool IsKnownSeparator(string? separator) => separator is Comma or Dot;

    /// <summary>The format of the stored separator; <see cref="Terminal"/> when none (or an unknown one) is stored.</summary>
    public static QuantityFormat FromOrganization(string? separator) => separator switch
    {
        Comma => CommaFormat,
        Dot => DotFormat,
        _ => Terminal,
    };
}
