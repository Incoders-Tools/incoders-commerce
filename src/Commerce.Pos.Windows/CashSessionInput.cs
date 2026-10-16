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
/// with the shared money rule (<see cref="MoneyInput"/>: "15.000" is fifteen
/// thousand, "15.000,50" and "15.50" carry cents), the live difference, and the
/// Spanish labels.
/// </summary>
public static class CashSessionInput
{
    /// <summary>The open-cash prompt names the signed-in operator, so it needs one: no label, no prompt.</summary>
    public static bool CanPromptOpenCash(string? operatorLabel) => !string.IsNullOrWhiteSpace(operatorLabel);

    public static AmountEntry ReadAmount(string? text)
    {
        var reading = MoneyInput.Read(text);
        return reading.Failure switch
        {
            MoneyReadFailure.Blank => new AmountEntry(false, null, null),
            MoneyReadFailure.Decimals => new AmountEntry(false, null, "Use como máximo dos decimales."),
            MoneyReadFailure.Format => new AmountEntry(false, null, "Ingrese un importe, por ejemplo 15.000 o 15.000,50."),
            _ when !CashSessionMath.IsValidAmount(reading.Amount!.Value) => new AmountEntry(false, reading.Amount, "Use como máximo dos decimales."),
            _ => new AmountEntry(true, reading.Amount, null),
        };
    }

    /// <summary>Under the opening float: "Se abrirá con $ 15.000,00"; null while nothing readable is typed.</summary>
    public static string? OpeningReadBack(string? text) =>
        ReadAmount(text) is { IsValid: true, Amount: { } amount } ? $"Se abrirá con {MoneyInput.Format(amount)}" : null;

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
