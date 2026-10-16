namespace Commerce.Domain.CurrentAccounts;

/// <summary>The business meaning of one ledger movement (PRD 9.13). `Reversal` is only created by reversing another movement.</summary>
public enum AccountMovementKind
{
    OpeningBalance,
    Invoice,
    DebitNote,
    CreditNote,
    Payment,
    Adjustment,
    Reversal,
}

/// <summary>Which side of the account a movement lands on. For a SUPPLIER account: Credit = owes more, Debit = owes less.</summary>
public enum AccountDirection
{
    Debit,
    Credit,
}

/// <summary>The facts of one movement the pure rules need (no I/O, no organization).</summary>
public sealed record AccountMovementFact(
    Guid Id,
    AccountMovementKind Kind,
    AccountDirection Direction,
    decimal Amount,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    Guid? ReversesMovementId);

/// <summary>Open overdue debt per age bucket, in days past the due date: 1-30, 31-60, 61-90 and more than 90.</summary>
public sealed record AgingBuckets(decimal D0To30, decimal D31To60, decimal D61To90, decimal D90Plus);

/// <summary>
/// `Balance`: what the business owes (negative = the business is in credit). `Overdue`: open debt past its due date
/// (the sum of <see cref="Aging"/>). `Current`: open debt not yet due or without a due date.
/// </summary>
public sealed record AccountSummary(decimal Balance, decimal Overdue, decimal Current, AgingBuckets Aging);

/// <summary>
/// Pure rules of a SUPPLIER current account.
/// <para>
/// SIGN CONVENTION: the balance is what the business owes the supplier = sum(Credit) - sum(Debit).
/// OpeningBalance (a positive debt), Invoice and DebitNote are Credit; Payment and CreditNote are Debit; Adjustment is
/// either, chosen explicitly; a Reversal is the OPPOSITE direction of the movement it reverses, same amount.
/// </para>
/// <para>
/// AGING / ALLOCATION RULE (<see cref="Summarize"/>), as of a date, over the movements dated on or before it:
/// (1) a movement reversed by a Reversal in force (dated on or before the date) is excluded together with its
/// reversal; (2) the open debt documents are the remaining Credit movements (Invoice, DebitNote, OpeningBalance and
/// credit Adjustments); (3) every remaining Debit movement (Payment, CreditNote, debit Adjustment) reduces that debt
/// FIFO: oldest due date first (documents without a due date last), ties by issue date then ledger order; a payment is
/// not tied to a specific document; (4) what stays open of each document is bucketed by whole days past its due date
/// (a document due today or later, or without a due date, is Current, not overdue); (5) payments beyond the total debt
/// leave a negative balance and nothing overdue.
/// </para>
/// </summary>
public static class CurrentAccountRules
{
    /// <summary>The direction implied by the kind, or null for the kinds whose direction is not fixed (Adjustment, Reversal).</summary>
    public static AccountDirection? FixedDirection(AccountMovementKind kind) => kind switch
    {
        AccountMovementKind.OpeningBalance or AccountMovementKind.Invoice or AccountMovementKind.DebitNote => AccountDirection.Credit,
        AccountMovementKind.Payment or AccountMovementKind.CreditNote => AccountDirection.Debit,
        _ => null,
    };

    /// <summary>
    /// Direction of a movement being REGISTERED: implied by the kind, explicit for an Adjustment (required), and a
    /// conflicting explicit direction on a fixed kind is refused. A Reversal is never registered directly.
    /// </summary>
    public static bool TryResolveDirection(
        AccountMovementKind kind, AccountDirection? requested, out AccountDirection direction, out string? error)
    {
        direction = default;
        error = null;

        if (kind == AccountMovementKind.Reversal)
        {
            error = "A Reversal is created by reversing a movement (POST .../reverse), not registered directly.";
            return false;
        }

        if (FixedDirection(kind) is { } fixedDirection)
        {
            if (requested is { } given && given != fixedDirection)
            {
                error = $"direction of a {kind} is always {fixedDirection}.";
                return false;
            }

            direction = fixedDirection;
            return true;
        }

        if (requested is null)
        {
            error = "direction (Debit or Credit) is required for an Adjustment.";
            return false;
        }

        direction = requested.Value;
        return true;
    }

    public static AccountDirection Opposite(AccountDirection direction) =>
        direction == AccountDirection.Credit ? AccountDirection.Debit : AccountDirection.Credit;

    /// <summary>The movement's effect on the balance: Credit adds, Debit subtracts.</summary>
    public static decimal SignedAmount(AccountDirection direction, decimal amount) =>
        direction == AccountDirection.Credit ? amount : -amount;

    /// <summary>Balance (credits minus debits) of the movements dated on or before <paramref name="asOf"/> (all when null).</summary>
    public static decimal Balance(IEnumerable<AccountMovementFact> movements, DateOnly? asOf = null) =>
        movements.Where(m => asOf is null || m.OccurredOn <= asOf).Sum(m => SignedAmount(m.Direction, m.Amount));

    /// <summary>The due date an invoice gets when none is sent: its date plus the supplier's payment terms (null without terms).</summary>
    public static DateOnly? DefaultDueOn(AccountMovementKind kind, DateOnly occurredOn, int? paymentTermsDays) =>
        kind == AccountMovementKind.Invoice && paymentTermsDays is { } days ? occurredOn.AddDays(days) : null;

    public static AccountSummary Summarize(IReadOnlyList<AccountMovementFact> movements, DateOnly asOf)
    {
        var inForce = movements.Where(m => m.OccurredOn <= asOf).ToList();
        var balance = Balance(inForce);

        var reversedIds = inForce
            .Where(m => m.Kind == AccountMovementKind.Reversal && m.ReversesMovementId is not null)
            .Select(m => m.ReversesMovementId!.Value)
            .ToHashSet();
        var live = inForce.Where(m => m.Kind != AccountMovementKind.Reversal && !reversedIds.Contains(m.Id)).ToList();

        var reductions = live.Where(m => m.Direction == AccountDirection.Debit).Sum(m => m.Amount);
        var documents = live
            .Select((m, index) => (Movement: m, Index: index))
            .Where(x => x.Movement.Direction == AccountDirection.Credit)
            .OrderBy(x => x.Movement.DueOn ?? DateOnly.MaxValue)
            .ThenBy(x => x.Movement.OccurredOn)
            .ThenBy(x => x.Index)
            .Select(x => x.Movement);

        decimal d0 = 0, d31 = 0, d61 = 0, d90 = 0, current = 0;
        foreach (var document in documents)
        {
            var applied = Math.Min(reductions, document.Amount);
            reductions -= applied;
            var open = document.Amount - applied;
            if (open == 0)
            {
                continue;
            }

            if (document.DueOn is not { } due || due >= asOf)
            {
                current += open;
                continue;
            }

            switch (asOf.DayNumber - due.DayNumber)
            {
                case <= 30: d0 += open; break;
                case <= 60: d31 += open; break;
                case <= 90: d61 += open; break;
                default: d90 += open; break;
            }
        }

        return new AccountSummary(balance, d0 + d31 + d61 + d90, current, new AgingBuckets(d0, d31, d61, d90));
    }
}
