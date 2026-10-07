using System.Globalization;
using Commerce.BranchNode;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Pricing;
using Commerce.Domain.Sales;

namespace Commerce.Pos.Windows;

/// <summary>What the payment prompt shows for what the cashier typed; <see cref="Tender"/> is set when it can be confirmed.</summary>
public sealed record CustomerPaymentEntry(bool IsValid, decimal? Amount, SaleTender? Tender, decimal? Change, string? Message);

/// <summary>
/// The pure logic of collecting a current account payment at the POS ("cobro"): reading the amount (decimal comma or
/// point, at most two decimals), the cash received and the change, and the Spanish texts that explain the customer's
/// balance and the due date of a sale on current account.
/// </summary>
public static class CustomerPaymentInput
{
    public static CustomerPaymentEntry Evaluate(string? amountText, string method, string? receivedText)
    {
        if (!TryReadAmount(amountText, out var amount))
        {
            return new CustomerPaymentEntry(false, null, null, null,
                string.IsNullOrWhiteSpace(amountText) ? null : "Ingrese el importe a cobrar, por ejemplo 15000 o 15000,50.");
        }

        if (amount <= 0m)
        {
            return new CustomerPaymentEntry(false, amount, null, null, "El importe debe ser mayor que cero.");
        }

        switch (method)
        {
            case SaleTender.Card:
                return new CustomerPaymentEntry(true, amount, SaleTenderRules.Card(), null, null);
            case SaleTender.Qr:
                return new CustomerPaymentEntry(true, amount, SaleTenderRules.Qr(), null, null);
            case SaleTender.Cash:
                var cash = TenderInput.EvaluateCash(amount, receivedText);
                return cash.IsValid && SaleTenderRules.TryCash(amount, cash.Received!.Value, out var tender)
                    ? new CustomerPaymentEntry(true, amount, tender, tender.ChangeGiven, null)
                    : new CustomerPaymentEntry(false, amount, null, null, cash.Message ?? (string.IsNullOrWhiteSpace(receivedText) ? null : "Revise el importe recibido."));
            default:
                return new CustomerPaymentEntry(false, amount, null, null, "Elija cómo paga el cliente: efectivo, tarjeta o QR.");
        }
    }

    internal static bool TryReadAmount(string? text, out decimal amount)
    {
        var normalized = (text ?? string.Empty).Trim().TrimStart('$').Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
               && Money.Round2(amount) == amount;
    }

    /// <summary>The amount to suggest: what the customer owes, when it owes something; otherwise empty.</summary>
    public static string SuggestedAmount(CustomerAccountView view) =>
        view.EstimatedBalance > 0m ? view.EstimatedBalance.ToString("0.00", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>"Debe $ 26.500,00" / "Saldo a favor $ 1.000,00" / "Sin deuda".</summary>
    public static string BalanceHeadline(CustomerAccountView view)
    {
        var culture = CultureInfo.CurrentCulture;
        return view.EstimatedBalance switch
        {
            > 0m => $"Debe {view.EstimatedBalance.ToString("C", culture)}",
            < 0m => $"Saldo a favor {(-view.EstimatedBalance).ToString("C", culture)}",
            _ => "Sin deuda",
        };
    }

    /// <summary>Where the balance comes from, so the cashier knows how fresh it is and what this terminal added.</summary>
    public static string BalanceDetail(CustomerAccountView view)
    {
        var culture = CultureInfo.CurrentCulture;
        if (view.SyncedBalance is null)
        {
            return "Todavía no se sincronizó la cuenta corriente de este cliente: el saldo solo incluye lo registrado en esta terminal.";
        }

        var parts = new List<string>
        {
            $"Saldo al {view.AsOfUtc?.ToLocalTime().ToString("dd/MM HH:mm", culture)}: {view.SyncedBalance.Value.ToString("C", culture)}",
        };
        if (view.SyncedOverdue is > 0m)
        {
            parts.Add($"vencido {view.SyncedOverdue.Value.ToString("C", culture)}");
        }

        if (view.PendingSalesOnAccount > 0m)
        {
            parts.Add($"ventas a cuenta desde entonces +{view.PendingSalesOnAccount.ToString("C", culture)}");
        }

        if (view.PendingPayments > 0m)
        {
            parts.Add($"cobros desde entonces −{view.PendingPayments.ToString("C", culture)}");
        }

        return string.Join(" · ", parts) + ".";
    }

    /// <summary>
    /// "Vence el 04/11/2026 (30 días, plazo general de la organización)." for a sale on current account made
    /// <paramref name="saleDate"/>; the cashier reads it before confirming.
    /// </summary>
    public static string DueText(PaymentTerms terms, DateOnly saleDate)
    {
        var due = terms.DueOn(saleDate).ToString("dd/MM/yyyy", CultureInfo.CurrentCulture);
        var source = terms.Source == PaymentTermsSource.Customer ? "plazo del cliente" : "plazo general de la organización";
        return terms.Days == 0
            ? $"Vence hoy, {due} (0 días, {source})."
            : $"Vence el {due} ({terms.Days} días, {source}).";
    }
}
