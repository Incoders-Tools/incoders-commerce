using System.Globalization;
using Commerce.Domain.CashSessions;

namespace Commerce.Pos.Windows;

/// <summary>What the open-cash prompt shows for a typed amount.</summary>
/// <param name="IsValid">The amount can be used.</param>
/// <param name="Amount">The parsed amount when it is valid.</param>
/// <param name="Message">Why it is refused; null when valid or while nothing is typed.</param>
public sealed record AmountEntry(bool IsValid, decimal? Amount, string? Message);

/// <summary>What the close-cash prompt shows for the counted cash typed.</summary>
/// <param name="Difference">Counted minus expected, only when the entry is valid.</param>
public sealed record CloseEntry(bool IsValid, decimal? Counted, decimal? Difference, string? Message);

/// <summary>
/// The pure logic behind the cash session prompts (pos-cash-session "Cash
/// Session Operator Interface"): reading the opening float or the counted cash
/// (Spanish keyboards type a decimal comma, so "1000,50" and "1000.50" are both
/// read, with an optional leading currency sign, and a thousands separator is
/// never accepted), the live difference, and the Spanish labels.
/// </summary>
public static class CashSessionInput
{
    public static AmountEntry ReadAmount(string? text)
    {
        var normalized = (text ?? string.Empty).Trim().TrimStart('$').Trim().Replace(',', '.');
        if (normalized.Length == 0)
        {
            return new AmountEntry(false, null, null);
        }

        // A second separator ("1,000.00") is a thousands separator: refused, like the tender prompt.
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
        {
            return new AmountEntry(false, null, "Ingrese un importe, por ejemplo 5000 o 5000,50.");
        }

        if (amount < 0m)
        {
            return new AmountEntry(false, amount, "El importe no puede ser negativo.");
        }

        if (!CashSessionMath.IsValidAmount(amount))
        {
            return new AmountEntry(false, amount, "Use como máximo dos decimales.");
        }

        return new AmountEntry(true, amount, null);
    }

    public static CloseEntry EvaluateClose(decimal expectedCash, string? text)
    {
        var entry = ReadAmount(text);
        return entry.IsValid
            ? new CloseEntry(true, entry.Amount, CashSessionMath.Difference(entry.Amount!.Value, expectedCash), null)
            : new CloseEntry(false, entry.Amount, null, entry.Message);
    }

    /// <summary>"Faltante $55.00", "Sobrante $45.00" or "Sin diferencia".</summary>
    public static string DifferenceLabel(decimal difference) => difference switch
    {
        < 0m => $"Faltante {Math.Abs(difference).ToString("C", CultureInfo.CurrentCulture)}",
        > 0m => $"Sobrante {difference.ToString("C", CultureInfo.CurrentCulture)}",
        _ => "Sin diferencia",
    };

    /// <summary>The compact header state, e.g. "Caja abierta · 08:15" (opening time, local).</summary>
    public static string HeaderText(CashSession? session) => session is { IsOpen: true }
        ? $"Caja abierta · {session.OpenedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"
        : "Caja cerrada";
}
