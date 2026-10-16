namespace Commerce.Domain.CurrentAccounts;

/// <summary>Whose current account a movement belongs to.</summary>
public enum AccountPartyKind
{
    /// <summary>What the business owes a supplier: an invoice is a Credit, a payment a Debit.</summary>
    Supplier,

    /// <summary>What a customer owes the business: a sale or delivery is a Debit, a payment received a Credit.</summary>
    Customer,

    /// <summary>
    /// What the business owes an employee, read like a supplier's account: the salary of a period is an Invoice (Credit),
    /// an advance, the goods bought and the salary paid are Debits. Negative: the employee owes the business.
    /// </summary>
    Employee,
}

/// <summary>
/// The current account rules for either party. <see cref="CurrentAccountRules"/> is written from the SUPPLIER side
/// (balance = credits - debits = what the business owes). A CUSTOMER account is its mirror: its movements are stored
/// with the receivable's natural direction (a sale on account is a Debit, a payment received a Credit) and its balance
/// is what the customer owes = debits - credits. Mirroring the directions turns one into the other, so the same
/// balance, statement and FIFO aging arithmetic serves both, and a customer's open debts (its debits) age exactly as a
/// supplier's invoices do.
/// </summary>
public static class PartyAccountRules
{
    /// <summary>The direction that increases what is owed on the party's account.</summary>
    public static AccountDirection DebtDirection(AccountPartyKind party) =>
        IsPayable(party) ? AccountDirection.Credit : AccountDirection.Debit;

    /// <summary>A party the business owes (supplier, employee): its account reads on the supplier side, unmirrored.</summary>
    public static bool IsPayable(AccountPartyKind party) => party is AccountPartyKind.Supplier or AccountPartyKind.Employee;

    /// <summary>The direction stored for <paramref name="party"/> given the supplier-side direction (and back: it is an involution).</summary>
    public static AccountDirection Mirror(AccountPartyKind party, AccountDirection direction) =>
        IsPayable(party) ? direction : CurrentAccountRules.Opposite(direction);

    /// <summary>The fact as the supplier-side rules read it.</summary>
    public static AccountMovementFact AsRulesFact(AccountPartyKind party, AccountMovementFact fact) =>
        IsPayable(party) ? fact : fact with { Direction = Mirror(party, fact.Direction) };

    /// <summary>
    /// The stored direction of a movement being REGISTERED on <paramref name="party"/>'s account: the kind's own
    /// direction for that party (an Invoice increases what is owed either way), or the explicit one of an Adjustment.
    /// <paramref name="requested"/> is in the party's own terms.
    /// </summary>
    public static bool TryResolveDirection(
        AccountPartyKind party, AccountMovementKind kind, AccountDirection? requested, out AccountDirection direction, out string? error)
    {
        var resolved = CurrentAccountRules.TryResolveDirection(
            kind, requested is { } given ? Mirror(party, given) : null, out var rulesDirection, out error);
        direction = resolved ? Mirror(party, rulesDirection) : default;
        if (!resolved && error is not null && party == AccountPartyKind.Customer && CurrentAccountRules.FixedDirection(kind) is { } fixedDirection)
        {
            error = $"direction of a {kind} is always {Mirror(party, fixedDirection)}.";
        }

        return resolved;
    }

    /// <summary>The movement's effect on what is owed: positive when it increases the debt.</summary>
    public static decimal SignedAmount(AccountPartyKind party, AccountDirection direction, decimal amount) =>
        direction == DebtDirection(party) ? amount : -amount;

    public static decimal Balance(AccountPartyKind party, IEnumerable<AccountMovementFact> movements, DateOnly? asOf = null) =>
        CurrentAccountRules.Balance(movements.Select(fact => AsRulesFact(party, fact)), asOf);

    public static AccountSummary Summarize(AccountPartyKind party, IReadOnlyList<AccountMovementFact> movements, DateOnly asOf) =>
        CurrentAccountRules.Summarize([.. movements.Select(fact => AsRulesFact(party, fact))], asOf);
}
