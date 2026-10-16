using System.Globalization;
using Commerce.Domain.Pricing;
using Commerce.Domain.Sales;

namespace Commerce.Pos.Windows;

/// <summary>What the cash prompt shows for what the cashier typed.</summary>
/// <param name="IsValid">The sale may be confirmed with this amount.</param>
/// <param name="Received">The parsed amount received, when it could be read.</param>
/// <param name="Change">The change to hand back, only when the entry is valid.</param>
/// <param name="Message">Why the entry is refused; null when valid or when nothing was typed yet.</param>
public sealed record CashEntry(bool IsValid, decimal? Received, decimal? Change, string? Message);

/// <summary>
/// The pure logic behind the tender prompts (pos-scan-sale "Tender Recorded at
/// the Moment of Sale"): reading the amount received with the shared money rule
/// (<see cref="MoneyInput"/>: "15.000" is fifteen thousand, "1000,50" and
/// "1000.50" carry cents), the change, the exact-amount fill and the Spanish labels.
/// </summary>
public static class TenderInput
{
    public static CashEntry EvaluateCash(decimal total, string? text)
    {
        var reading = MoneyInput.Read(text);
        switch (reading.Failure)
        {
            case MoneyReadFailure.Blank:
                return new CashEntry(false, null, null, null);
            case MoneyReadFailure.Decimals:
                return new CashEntry(false, null, null, "Use como máximo dos decimales.");
            case MoneyReadFailure.Format:
                return new CashEntry(false, null, null, "Ingrese el importe recibido, por ejemplo 15.000 o 15.000,50.");
        }

        var received = reading.Amount!.Value;
        if (Money.Round2(received) != received)
        {
            return new CashEntry(false, received, null, "Use como máximo dos decimales.");
        }

        if (!SaleTenderRules.TryCash(total, received, out var tender))
        {
            return new CashEntry(false, received, null, "El importe recibido es menor que el total.");
        }

        return new CashEntry(true, received, tender.ChangeGiven, null);
    }

    /// <summary>The text that fills "amount received" with exactly the total ("Exacto").</summary>
    public static string ExactText(decimal total) => total.ToString("0.00", CultureInfo.CurrentCulture);

    public static string Label(string method) => method switch
    {
        SaleTender.Cash => "Efectivo",
        SaleTender.Card => "Tarjeta",
        SaleTender.Qr => "QR",
        SaleTender.Account => "Cuenta corriente",
        _ => method,
    };

    /// <summary>One line for the sale result: the method and, for cash, what was received and the change.</summary>
    public static string Describe(SaleTender tender) =>
        tender is { Method: SaleTender.Cash, AmountReceived: { } received, ChangeGiven: { } change }
            ? $"{Label(tender.Method)}, recibido {received.ToString("C", CultureInfo.CurrentCulture)}, vuelto {change.ToString("C", CultureInfo.CurrentCulture)}"
            : Label(tender.Method);
}
