using System.Globalization;
using System.Text.RegularExpressions;

namespace Commerce.Pos.Windows;

/// <summary>Why a money text is not an amount.</summary>
public enum MoneyReadFailure
{
    None,
    Blank,
    Format,
    Decimals,
}

/// <summary>What a money field read: the amount when valid, otherwise why not.</summary>
public readonly record struct MoneyReading(decimal? Amount, MoneyReadFailure Failure)
{
    public bool IsValid => Failure == MoneyReadFailure.None;
}

/// <summary>
/// The one way every POS money field reads what was typed, in es-AR: the web's <c>readMoney</c> (owner feedback
/// 2026-10-15 round 2, <c>docs/architecture/cross-layer-parity.md</c>). An amount carries at most a few decimals, so a
/// point is never ambiguous: a "." followed by exactly three digits groups thousands ("15.000" = 15000,
/// "1.234.567,50"); a "," or a "." followed by one or two digits is the decimal ("15,5", "15.50"); anything else is
/// refused ("1,234.56", "15.0000", a sign). A leading "$" and surrounding spaces are ignored.
/// </summary>
public static partial class MoneyInput
{
    /// <summary>The example every money field shows, the web's.</summary>
    public const string Placeholder = "Ej.: 15.000 o 15.000,50";

    // [0-9], never \d: .NET's \d also matches Arabic-Indic or full-width digits, which decimal.Parse then refuses.
    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{3})+(,[0-9]+)?$")]
    private static partial Regex Grouped();

    [GeneratedRegex(@"^[0-9]+(,[0-9]+)?$")]
    private static partial Regex Plain();

    [GeneratedRegex(@"^[0-9]+\.[0-9]{1,2}$")]
    private static partial Regex PointDecimal();

    public static MoneyReading Read(string? text, int maxDecimals = 2)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith('$'))
        {
            value = value[1..].Trim();
        }

        if (value.Length == 0)
        {
            return new MoneyReading(null, MoneyReadFailure.Blank);
        }

        string normalized;
        if (Grouped().IsMatch(value))
        {
            normalized = value.Replace(".", string.Empty).Replace(',', '.');
        }
        else if (Plain().IsMatch(value))
        {
            normalized = value.Replace(',', '.');
        }
        else if (PointDecimal().IsMatch(value))
        {
            normalized = value;
        }
        else
        {
            return new MoneyReading(null, MoneyReadFailure.Format);
        }

        var point = normalized.IndexOf('.');
        if (point >= 0 && normalized.Length - point - 1 > maxDecimals)
        {
            return new MoneyReading(null, MoneyReadFailure.Decimals);
        }

        return new MoneyReading(decimal.Parse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), MoneyReadFailure.None);
    }

    /// <summary><see cref="Read"/> for callers that only need the amount.</summary>
    public static bool TryRead(string? text, out decimal amount, int maxDecimals = 2)
    {
        var reading = Read(text, maxDecimals);
        amount = reading.Amount ?? 0m;
        return reading.IsValid;
    }

    /// <summary>"$ 15.000,00", in es-AR whatever the Windows language.</summary>
    public static string Format(decimal amount) => amount.ToString("C2", PosCulture.Spanish);
}
