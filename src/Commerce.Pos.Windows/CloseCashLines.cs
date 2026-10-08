using System.Globalization;
using Commerce.Domain.CashSessions;

namespace Commerce.Pos.Windows;

/// <summary>One row of the close-cash summary: a label on the left, its amount on the right, an optional hint below.</summary>
public sealed record CloseCashLine(string Label, string Amount, string? Hint = null)
{
    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

/// <summary>
/// What the close-cash dialog lists, UI-free: the session's sales by tender, the money that moved outside a sale, and
/// how the expected cash is made up, one row per addend, so no row ever packs two amounts side by side.
/// </summary>
public static class CloseCashLines
{
    private static string Money(decimal amount) => amount.ToString("C", CultureInfo.CurrentCulture);

    /// <summary>How many sales, and what was charged by card, QR and on account (which never enters the drawer).</summary>
    public static IReadOnlyList<CloseCashLine> Sales(CashSessionSummary summary) =>
    [
        new("Cantidad de ventas", summary.SaleCount.ToString(CultureInfo.CurrentCulture)),
        new("Con tarjeta", Money(summary.CardTotal)),
        new("Con QR", Money(summary.QrTotal)),
        new("A cuenta corriente", Money(summary.AccountTotal), "No entra en la caja: el cliente lo debe."),
    ];

    /// <summary>The customer payments collected and the drawer movements; empty rows are left out.</summary>
    public static IReadOnlyList<CloseCashLine> OtherMoney(CashSessionSummary summary)
    {
        var lines = new List<CloseCashLine>();
        var collected = summary.CollectedCash + summary.CollectedCard + summary.CollectedQr;
        lines.Add(summary.CollectionCount == 0
            ? new("Cobros de cuenta corriente", Money(0m))
            : new("Cobros de cuenta corriente", Money(collected),
                $"{summary.CollectionCount} {(summary.CollectionCount == 1 ? "cobro" : "cobros")}: efectivo {Money(summary.CollectedCash)}"
                + (summary.CollectedCard > 0m ? $", tarjeta {Money(summary.CollectedCard)}" : string.Empty)
                + (summary.CollectedQr > 0m ? $", QR {Money(summary.CollectedQr)}" : string.Empty)));
        lines.Add(new("Retiros de caja", summary.CashWithdrawn > 0m ? $"−{Money(summary.CashWithdrawn)}" : "—",
            summary.CashWithdrawn > 0m ? "A la caja fuerte, al banco o gastos." : null));
        lines.Add(new("Ingresos de caja", summary.CashDeposited > 0m ? $"+{Money(summary.CashDeposited)}" : "—"));
        return lines;
    }

    /// <summary>
    /// The expected cash, as a sum: the opening float, the cash kept from sales (change already given), the cash
    /// collected, the cash put in, minus the cash taken out. Rows that add nothing are left out.
    /// </summary>
    public static IReadOnlyList<CloseCashLine> ExpectedCash(CashSessionSummary summary)
    {
        var lines = new List<CloseCashLine>
        {
            new("Efectivo inicial", Money(summary.OpeningFloat)),
            new("+ Efectivo de ventas", Money(summary.CashKept), "Ya descontado el vuelto."),
        };
        if (summary.CollectedCash > 0m) lines.Add(new("+ Cobros en efectivo", Money(summary.CollectedCash)));
        if (summary.CashDeposited > 0m) lines.Add(new("+ Ingresos", Money(summary.CashDeposited)));
        if (summary.CashWithdrawn > 0m) lines.Add(new("− Retiros", Money(summary.CashWithdrawn)));
        return lines;
    }
}
