using Commerce.Domain.Payments;

namespace Commerce.Application.Payments;

/// <summary>
/// The ONE place partial-payment arithmetic lives (commerce-payments
/// design.md "Where the arithmetic lives"), mirroring
/// <c>PricingResolutionService</c>'s "one server-side policy" shape. Pure: no
/// I/O, no persistence call. <c>decimal</c> end to end — <c>double</c>
/// appears nowhere.
/// </summary>
public static class SettlementCalculator
{
    /// <summary>
    /// Folds <paramref name="entries"/> into a derived <see cref="Settlement"/>.
    /// A <see cref="PaymentEntryKind.Payment"/> entry with
    /// <see cref="PaymentApprovalState.Approved"/> adds to settled; a
    /// <see cref="PaymentEntryKind.Reversal"/> entry (always Approved — it is
    /// the record of an undo, never itself declined) subtracts. A
    /// <see cref="PaymentApprovalState.Declined"/> or
    /// <see cref="PaymentApprovalState.Unavailable"/> Payment entry never
    /// contributes — it is history only, never money. Never mutates or
    /// filters <paramref name="entries"/>; every entry remains visible to the
    /// caller afterward.
    /// </summary>
    public static Settlement Fold(decimal target, IReadOnlyList<PaymentEntry> entries)
    {
        var settled = 0m;
        foreach (var entry in entries)
        {
            if (entry.ApprovalState != PaymentApprovalState.Approved)
            {
                continue;
            }

            settled += entry.Kind switch
            {
                PaymentEntryKind.Payment => entry.Amount,
                PaymentEntryKind.Reversal => -entry.Amount,
                _ => 0m
            };
        }

        var outstanding = target - settled;
        return new Settlement(target, settled, outstanding, IsSettled: outstanding <= 0m);
    }

    /// <summary>
    /// Report-only, derived, NEVER stored, NEVER consulted by <see cref="Fold"/>:
    /// largest-remainder (Hamilton) allocation of <paramref name="amount"/>
    /// across <paramref name="frozenLineTotals"/>, ties broken by ascending
    /// line index. <c>Sum(result) == amount</c> exactly by construction
    /// (commerce-payments design.md "Rounding / allocation policy").
    /// </summary>
    public static IReadOnlyList<decimal> AllocateForReport(decimal amount, IReadOnlyList<decimal> frozenLineTotals)
    {
        var lineTotalSum = frozenLineTotals.Sum();
        if (lineTotalSum == 0m || frozenLineTotals.Count == 0)
        {
            return frozenLineTotals.Select(_ => 0m).ToList();
        }

        // Base allocation truncated to 2 decimals per line, tracking each
        // line's fractional remainder for largest-remainder distribution.
        var bases = new decimal[frozenLineTotals.Count];
        var remainders = new decimal[frozenLineTotals.Count];
        var baseSum = 0m;

        for (var i = 0; i < frozenLineTotals.Count; i++)
        {
            var exact = amount * frozenLineTotals[i] / lineTotalSum;
            var truncated = Math.Floor(exact * 100m) / 100m;
            bases[i] = truncated;
            remainders[i] = exact - truncated;
            baseSum += truncated;
        }

        // The leftover cents (amount - baseSum, in whole cents) go to the
        // lines with the largest remainders, ties broken by ascending index.
        var leftoverCents = (int)Math.Round((amount - baseSum) * 100m, MidpointRounding.AwayFromZero);

        var order = Enumerable.Range(0, frozenLineTotals.Count)
            .OrderByDescending(i => remainders[i])
            .ThenBy(i => i)
            .ToList();

        for (var i = 0; i < leftoverCents && i < order.Count; i++)
        {
            bases[order[i]] += 0.01m;
        }

        return bases.ToList();
    }
}
