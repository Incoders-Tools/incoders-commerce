using System.Globalization;
using Commerce.BranchNode;
using Commerce.Domain.Sync;

namespace Commerce.Pos.Windows;

/// <summary>One line of the POS history: a sale or a customer payment ("cobro"). What the list template binds to.</summary>
public interface IHistoryRow
{
    Guid Id { get; }

    DateTimeOffset OccurredAtUtc { get; }

    bool IsVoided { get; }

    bool CanVoid { get; }

    string TimeText { get; }

    string NumberText { get; }

    string CustomerText { get; }

    string TenderText { get; }

    string KindText { get; }

    string TotalText { get; }

    string StatusText { get; }

    decimal Amount { get; }
}

/// <summary>One sale of the POS sales history as the list shows it. <see cref="CanVoid"/> is the rule, not a guess.</summary>
public sealed record SaleHistoryRow(SaleHistoryEntry Entry, bool CanVoid) : IHistoryRow
{
    public Guid SaleId => Entry.SaleId;

    public Guid Id => Entry.SaleId;

    public DateTimeOffset OccurredAtUtc => Entry.OccurredAtUtc;

    public decimal Amount => Entry.TotalAmount;

    public bool IsVoided => Entry.Void is not null;

    public string TimeText => Entry.OccurredAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string NumberText => Entry.Number ?? "Sin número";

    public string CustomerText => Entry.CustomerName
        ?? (Entry.CustomerId is null ? SaleCustomerPicker.WalkInLabel : "Cliente no sincronizado");

    public string TenderText => SaleHistory.TenderText(Entry.Tender?.Method);

    public string TotalText => Entry.TotalAmount.ToString("C", CultureInfo.CurrentCulture);

    public string KindText => Entry.SaleKind == "Manual" ? "Venta manual" : $"{Entry.LineCount} {(Entry.LineCount == 1 ? "producto" : "productos")}";

    public string StatusText => IsVoided ? "Anulada" : string.Empty;
}

/// <summary>
/// Money taken out of or put into the drawer outside a sale, in the POS history. Not voided at the terminal: the cashier
/// records the opposite movement, or an administrator voids or edits it in the treasury.
/// </summary>
public sealed record CashMovementHistoryRow(CashMovementRecord Movement) : IHistoryRow
{
    public Guid Id => Movement.MovementId;

    public DateTimeOffset OccurredAtUtc => Movement.OccurredAtUtc;

    public bool IsVoided => false;

    public bool CanVoid => false;

    public bool IsWithdrawal => Movement.Kind == Commerce.Domain.CashSessions.CashMovement.Withdrawal;

    public decimal Amount => Movement.Amount;

    public string TimeText => Movement.OccurredAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string NumberText => IsWithdrawal ? "Retiro de caja" : "Ingreso de caja";

    public string CustomerText => Movement.Description;

    public string TenderText => "Efectivo";

    public string KindText => Movement.Reason;

    public string TotalText => (IsWithdrawal ? "−" : "+") + Movement.Amount.ToString("C", CultureInfo.CurrentCulture);

    public string StatusText => string.Empty;
}

/// <summary>One customer payment of current account debt ("cobro") in the POS history.</summary>
public sealed record PaymentHistoryRow(CustomerPaymentRecord Payment, bool CanVoid) : IHistoryRow
{
    public Guid Id => Payment.PaymentId;

    public DateTimeOffset OccurredAtUtc => Payment.ReceivedAtUtc;

    public bool IsVoided => Payment.Void is not null;

    public decimal Amount => Payment.Amount;

    public string TimeText => Payment.ReceivedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    public string NumberText => "Cobro cta. cte.";

    public string CustomerText => Payment.CustomerName ?? "Cliente no sincronizado";

    public string TenderText => SaleHistory.TenderText(Payment.Tender.Method);

    public string KindText => Payment.Note is { Length: > 0 } note ? note : "Pago de cuenta corriente";

    public string TotalText => Payment.Amount.ToString("C", CultureInfo.CurrentCulture);

    public string StatusText => IsVoided ? "Anulado" : string.Empty;
}

/// <summary>
/// The POS sales history, UI-free: the rows of a day newest first, the search, the detail texts and the void rule. A sale
/// can be voided only while its cash session is the open one (a closed session's totals were frozen and reported at
/// close), and only once.
/// </summary>
public static class SaleHistory
{
    /// <summary>The local day [<paramref name="day"/> 00:00, next day 00:00) in UTC, as the store filters it.</summary>
    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) DayRange(DateOnly day, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(start, zone.GetUtcOffset(start));
        var nextStart = start.AddDays(1);
        var to = new DateTimeOffset(nextStart, zone.GetUtcOffset(nextStart));
        return (from.ToUniversalTime(), to.ToUniversalTime());
    }

    public static bool CanVoid(SaleHistoryEntry entry, Guid? openCashSessionId) =>
        entry.Void is null && entry.CashSessionId is { } session && session == openCashSessionId;

    public static IReadOnlyList<SaleHistoryRow> Rows(IReadOnlyList<SaleHistoryEntry> entries, Guid? openCashSessionId) =>
        entries.Select(entry => new SaleHistoryRow(entry, CanVoid(entry, openCashSessionId))).ToList();

    /// <summary>The day's sales and customer payments together, newest first; each voidable only while its session is open.</summary>
    public static IReadOnlyList<IHistoryRow> Rows(
        IReadOnlyList<SaleHistoryEntry> sales, IReadOnlyList<CustomerPaymentRecord> payments, Guid? openCashSessionId,
        IReadOnlyList<CashMovementRecord>? cashMovements = null) =>
        Rows(sales, openCashSessionId).Cast<IHistoryRow>()
            .Concat(payments.Select(payment => new PaymentHistoryRow(
                payment, payment.Void is null && payment.CashSessionId is { } session && session == openCashSessionId)))
            .Concat((cashMovements ?? []).Select(movement => new CashMovementHistoryRow(movement)))
            .OrderByDescending(row => row.OccurredAtUtc)
            .ToList();

    /// <summary>Rows whose number, customer, tender or total contain every word of <paramref name="term"/> (case and accents ignored).</summary>
    public static IReadOnlyList<TRow> Filter<TRow>(IReadOnlyList<TRow> rows, string? term)
        where TRow : IHistoryRow
    {
        var words = BranchSyncStore.FoldText(term).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return rows;
        }

        return rows
            .Where(row =>
            {
                var text = BranchSyncStore.FoldText(
                    $"{row.NumberText} {row.CustomerText} {row.TenderText} {row.TotalText} {row.Amount.ToString(CultureInfo.InvariantCulture)} {row.StatusText} {row.KindText}");
                return words.All(word => text.Contains(word, StringComparison.Ordinal));
            })
            .ToList();
    }

    public static string TenderText(string? method) => method switch
    {
        "cash" => "Efectivo",
        "card" => "Tarjeta",
        "qr" => "QR",
        "account" => "Cuenta corriente",
        _ => "Sin medio",
    };

    /// <summary>
    /// "3 ventas · $ 45.000,00" over the sales that count (voided ones excluded), then the customer payments collected
    /// ("2 cobros · $ 30.000,00") when there are any, then the voided count.
    /// </summary>
    public static string Summary<TRow>(IReadOnlyList<TRow> rows)
        where TRow : IHistoryRow
    {
        var culture = CultureInfo.CurrentCulture;
        var sales = rows.OfType<SaleHistoryRow>().Where(row => !row.IsVoided).ToList();
        var payments = rows.OfType<PaymentHistoryRow>().Where(row => !row.IsVoided).ToList();
        var text = $"{sales.Count} {(sales.Count == 1 ? "venta" : "ventas")} · {sales.Sum(row => row.Amount).ToString("C", culture)}";
        if (payments.Count > 0)
        {
            text += $" · {payments.Count} {(payments.Count == 1 ? "cobro" : "cobros")} · {payments.Sum(row => row.Amount).ToString("C", culture)}";
        }

        var movements = rows.OfType<CashMovementHistoryRow>().ToList();
        if (movements.Count > 0)
        {
            text += $" · {movements.Count} {(movements.Count == 1 ? "movimiento de caja" : "movimientos de caja")}";
        }

        var voided = rows.Count(row => row.IsVoided);
        return voided == 0 ? text : $"{text} · {voided} {(voided == 1 ? "anulado" : "anulados")}";
    }

    /// <summary>Why a sale or payment cannot be voided right now, for the detail panel; null when it can.</summary>
    public static string? VoidUnavailableReason(IHistoryRow row) => row switch
    {
        { IsVoided: true } => null,
        { CanVoid: true } => null,
        SaleHistoryRow { Entry.CashSessionId: null } => "Esta venta no pertenece a una caja: no se puede anular.",
        SaleHistoryRow => "Solo se pueden anular ventas de la caja abierta. Esta venta es de una caja ya cerrada.",
        _ => "Solo se pueden anular cobros de la caja abierta. Este cobro es de una caja ya cerrada.",
    };

    /// <summary>What the operator reads after trying to void a customer payment.</summary>
    public static string PaymentVoidOutcomeMessage(SaleVoidOutcome outcome) => outcome switch
    {
        SaleVoidOutcome.Voided => "Cobro anulado. La caja ya no lo cuenta y la deuda del cliente vuelve al sincronizar.",
        SaleVoidOutcome.AlreadyVoided => "El cobro ya estaba anulado.",
        SaleVoidOutcome.SessionNotOpen => "Solo se pueden anular cobros de la caja abierta.",
        _ => "El cobro no existe en esta terminal.",
    };

    /// <summary>What the operator reads after trying to void.</summary>
    public static string VoidOutcomeMessage(SaleVoidOutcome outcome, string number) => outcome switch
    {
        SaleVoidOutcome.Voided => $"Venta {number} anulada. La caja ya no la cuenta.",
        SaleVoidOutcome.AlreadyVoided => $"La venta {number} ya estaba anulada.",
        SaleVoidOutcome.SessionNotOpen => "Solo se pueden anular ventas de la caja abierta.",
        _ => "La venta no existe en esta terminal.",
    };

    /// <summary>"2 x Vacío (Por kg) a $ 9.800,00 = $ 19.600,00", with the line discount when it has one.</summary>
    public static string LineText(SaleLine line)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = $"{line.Quantity.ToString("0.###", culture)} × {line.ProductName} ({line.PresentationName}) a " +
                   $"{line.UnitPrice.ToString("C", culture)} = {line.LineTotal.ToString("C", culture)}";
        return line.LineDiscountPercent is { } percent
            ? $"{text} · desc. {percent.ToString("0.##", culture)} % (−{(line.LineDiscountAmount ?? 0m).ToString("C", culture)})"
            : text;
    }
}
