using System.Globalization;
using Commerce.BranchNode;
using Commerce.Domain.CashSessions;

namespace Commerce.Pos.Windows;

/// <summary>What the cash movement prompt read: valid or why not.</summary>
public sealed record CashMovementEntry(bool IsValid, decimal? Amount, string? Message);

/// <summary>
/// The cash movement prompt, UI-free: the amount (at most two decimals, more than zero, and for a withdrawal no more than
/// the drawer should hold), the reason, and the texts the cashier reads.
/// </summary>
public static class CashMovementInput
{
    public static CashMovementEntry Evaluate(string kind, string? amountText, string? reason, decimal expectedCash)
    {
        if (!CustomerPaymentInput.TryReadAmount(amountText, out var amount))
        {
            return new CashMovementEntry(false, null,
                string.IsNullOrWhiteSpace(amountText) ? null : "Ingrese el importe, por ejemplo 15000 o 15000,50.");
        }

        if (amount <= 0m)
        {
            return new CashMovementEntry(false, amount, "El importe debe ser mayor que cero.");
        }

        if (kind == CashMovement.Withdrawal && amount > expectedCash)
        {
            return new CashMovementEntry(false, amount,
                $"No se puede retirar más de lo que debería haber en la caja ({expectedCash.ToString("C", CultureInfo.CurrentCulture)}).");
        }

        var text = (reason ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new CashMovementEntry(false, amount, null);
        }

        return text.Length > CashMovement.MaxReasonLength
            ? new CashMovementEntry(false, amount, $"El motivo puede tener hasta {CashMovement.MaxReasonLength} caracteres.")
            : new CashMovementEntry(true, amount, null);
    }

    /// <summary>What happens with the money, for the info text under the form.</summary>
    public static string Explanation(string kind, string counterpart) => (kind, counterpart) switch
    {
        (CashMovement.Withdrawal, CashMovement.Safe) =>
            "Sale de la caja y entra en la caja fuerte de la sucursal (en Tesorería se ve como una transferencia).",
        (CashMovement.Withdrawal, CashMovement.Bank) =>
            "Sale de la caja para llevarla al banco. Cuando se deposita, la administración registra el ingreso en la cuenta del banco.",
        (CashMovement.Withdrawal, CashMovement.Expense) =>
            "Sale de la caja para pagar un gasto. Anotá en el motivo qué se pagó (y el comprobante, si hay).",
        (CashMovement.Withdrawal, _) => "Sale de la caja. Explicá en el motivo para qué.",
        (_, CashMovement.Safe) => "Entra en la caja desde la caja fuerte de la sucursal (por ejemplo, cambio).",
        _ => "Entra en la caja. Explicá en el motivo de dónde viene el dinero.",
    };

    /// <summary>"Retiro registrado: $ 5.000,00 (Retiro a caja fuerte). Efectivo esperado: $ 12.000,00."</summary>
    public static string ResultText(CashMovementResult result, decimal? expectedCash) => result.Outcome switch
    {
        CashMovementOutcome.Recorded when result.Movement is { } movement =>
            $"{(movement.Kind == CashMovement.Withdrawal ? "Retiro" : "Ingreso")} registrado: " +
            $"{movement.Amount.ToString("C", CultureInfo.CurrentCulture)} ({movement.Description})." +
            (expectedCash is { } expected ? $" Efectivo esperado en caja: {expected.ToString("C", CultureInfo.CurrentCulture)}." : string.Empty),
        CashMovementOutcome.ExceedsExpectedCash => "No se puede retirar más de lo que debería haber en la caja.",
        _ => "No hay una caja abierta: abra la caja para registrar movimientos.",
    };
}
