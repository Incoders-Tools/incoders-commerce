using Commerce.Domain.CurrentAccounts;

namespace Commerce.Integration;

/// <summary>
/// Pure current-account rules (no I/O): the kind -> direction mapping, the signed balance of a supplier account
/// (credits minus debits = what the business owes), the default due date and the FIFO aging of the open debt.
/// </summary>
public sealed class CurrentAccountRulesTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 2);

    private static AccountMovementFact Fact(
        AccountMovementKind kind, decimal amount, DateOnly occurredOn, DateOnly? dueOn = null, Guid? id = null,
        Guid? reverses = null, AccountDirection? direction = null) =>
        new(id ?? Guid.NewGuid(), kind,
            direction ?? CurrentAccountRules.FixedDirection(kind) ?? AccountDirection.Credit,
            amount, occurredOn, dueOn, reverses);

    private static AccountMovementFact Reversal(AccountMovementFact original, DateOnly on) =>
        Fact(AccountMovementKind.Reversal, original.Amount, on,
            reverses: original.Id, direction: CurrentAccountRules.Opposite(original.Direction));

    [Theory]
    [InlineData(AccountMovementKind.OpeningBalance, AccountDirection.Credit)]
    [InlineData(AccountMovementKind.Invoice, AccountDirection.Credit)]
    [InlineData(AccountMovementKind.DebitNote, AccountDirection.Credit)]
    [InlineData(AccountMovementKind.Payment, AccountDirection.Debit)]
    [InlineData(AccountMovementKind.CreditNote, AccountDirection.Debit)]
    public void FixedKinds_HaveTheirSupplierAccountDirection(AccountMovementKind kind, AccountDirection expected)
    {
        Assert.Equal(expected, CurrentAccountRules.FixedDirection(kind));
        Assert.True(CurrentAccountRules.TryResolveDirection(kind, null, out var direction, out var error));
        Assert.Equal(expected, direction);
        Assert.Null(error);
    }

    [Fact]
    public void Adjustment_RequiresAnExplicitDirection_AndFixedKindsRejectAConflictingOne()
    {
        Assert.False(CurrentAccountRules.TryResolveDirection(AccountMovementKind.Adjustment, null, out _, out var missing));
        Assert.Contains("direction", missing);
        Assert.True(CurrentAccountRules.TryResolveDirection(AccountMovementKind.Adjustment, AccountDirection.Debit, out var debit, out _));
        Assert.Equal(AccountDirection.Debit, debit);

        Assert.False(CurrentAccountRules.TryResolveDirection(AccountMovementKind.Payment, AccountDirection.Credit, out _, out var conflict));
        Assert.Contains("Payment", conflict);
        Assert.True(CurrentAccountRules.TryResolveDirection(AccountMovementKind.Payment, AccountDirection.Debit, out _, out _));
    }

    [Fact]
    public void Reversal_CannotBeCreatedDirectly()
    {
        Assert.False(CurrentAccountRules.TryResolveDirection(AccountMovementKind.Reversal, AccountDirection.Debit, out _, out var error));
        Assert.Contains("reverse", error);
    }

    [Fact]
    public void Opposite_FlipsTheDirection()
    {
        Assert.Equal(AccountDirection.Debit, CurrentAccountRules.Opposite(AccountDirection.Credit));
        Assert.Equal(AccountDirection.Credit, CurrentAccountRules.Opposite(AccountDirection.Debit));
    }

    [Fact]
    public void Balance_IsCreditsMinusDebits_AndAReversalCancelsItsOriginal()
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100_000m, new(2026, 9, 1));
        var payment = Fact(AccountMovementKind.Payment, 40_000m, new(2026, 9, 5));
        Assert.Equal(60_000m, CurrentAccountRules.Balance([invoice, payment]));

        var reversal = Reversal(payment, new(2026, 9, 6));
        Assert.Equal(100_000m, CurrentAccountRules.Balance([invoice, payment, reversal]));
        Assert.Equal(0m, CurrentAccountRules.Balance([]));
    }

    [Fact]
    public void Balance_AsOf_IgnoresLaterMovements()
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100m, new(2026, 9, 1));
        var payment = Fact(AccountMovementKind.Payment, 40m, new(2026, 9, 20));
        Assert.Equal(100m, CurrentAccountRules.Balance([invoice, payment], new DateOnly(2026, 9, 10)));
        Assert.Equal(60m, CurrentAccountRules.Balance([invoice, payment], new DateOnly(2026, 9, 20)));
    }

    [Theory]
    [InlineData(30, 2026, 10, 31)]
    [InlineData(0, 2026, 10, 1)]
    public void DefaultDueOn_ForAnInvoice_IsTheInvoiceDatePlusThePaymentTerms(int terms, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), CurrentAccountRules.DefaultDueOn(AccountMovementKind.Invoice, new(2026, 10, 1), terms));
    }

    [Fact]
    public void DefaultDueOn_IsNull_WithoutTerms_OrForOtherKinds()
    {
        Assert.Null(CurrentAccountRules.DefaultDueOn(AccountMovementKind.Invoice, new(2026, 10, 1), null));
        Assert.Null(CurrentAccountRules.DefaultDueOn(AccountMovementKind.Payment, new(2026, 10, 1), 30));
    }

    [Fact]
    public void Summary_OfAnEmptyAccount_IsZero()
    {
        var summary = CurrentAccountRules.Summarize([], AsOf);
        Assert.Equal(0m, summary.Balance);
        Assert.Equal(0m, summary.Overdue);
        Assert.Equal(0m, summary.Aging.D0To30 + summary.Aging.D31To60 + summary.Aging.D61To90 + summary.Aging.D90Plus);
    }

    [Theory]
    [InlineData(1, 100, 0, 0, 0)]   // 1 day overdue
    [InlineData(30, 100, 0, 0, 0)]
    [InlineData(31, 0, 100, 0, 0)]
    [InlineData(60, 0, 100, 0, 0)]
    [InlineData(61, 0, 0, 100, 0)]
    [InlineData(90, 0, 0, 100, 0)]
    [InlineData(91, 0, 0, 0, 100)]
    [InlineData(400, 0, 0, 0, 100)]
    public void OverdueInvoice_LandsInTheBucketOfItsDaysPastDue(int daysOverdue, decimal b0, decimal b31, decimal b61, decimal b90)
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100m, AsOf.AddDays(-daysOverdue - 30), AsOf.AddDays(-daysOverdue));

        var summary = CurrentAccountRules.Summarize([invoice], AsOf);

        Assert.Equal(100m, summary.Balance);
        Assert.Equal(100m, summary.Overdue);
        Assert.Equal(0m, summary.Current);
        Assert.Equal(new AgingBuckets(b0, b31, b61, b90), summary.Aging);
    }

    [Fact]
    public void InvoiceDueTodayOrLater_IsCurrent_NotOverdue()
    {
        var dueToday = Fact(AccountMovementKind.Invoice, 10m, AsOf.AddDays(-30), AsOf);
        var dueLater = Fact(AccountMovementKind.Invoice, 20m, AsOf, AsOf.AddDays(30));
        var noDueDate = Fact(AccountMovementKind.Invoice, 5m, AsOf.AddDays(-100));

        var summary = CurrentAccountRules.Summarize([dueToday, dueLater, noDueDate], AsOf);

        Assert.Equal(35m, summary.Balance);
        Assert.Equal(0m, summary.Overdue);
        Assert.Equal(35m, summary.Current);
    }

    [Fact]
    public void Payments_AreAppliedFifoByDueDate_OldestDebtFirst()
    {
        var older = Fact(AccountMovementKind.Invoice, 100m, new(2026, 7, 1), new(2026, 8, 1));   // 62 days overdue
        var newer = Fact(AccountMovementKind.Invoice, 50m, new(2026, 8, 20), new(2026, 9, 15));  // 17 days overdue
        var payment = Fact(AccountMovementKind.Payment, 120m, new(2026, 9, 20));

        // Listed newest first on purpose: the allocation follows the due date, not the list order.
        var summary = CurrentAccountRules.Summarize([newer, older, payment], AsOf);

        Assert.Equal(30m, summary.Balance);
        Assert.Equal(30m, summary.Overdue);
        Assert.Equal(new AgingBuckets(30m, 0m, 0m, 0m), summary.Aging);
    }

    [Fact]
    public void AcceptanceScenario_PartialPayment_ThenItsReversal()
    {
        var today = AsOf;
        var invoice = Fact(AccountMovementKind.Invoice, 100_000m, today, today.AddDays(30));
        var payment = Fact(AccountMovementKind.Payment, 40_000m, today);

        var paid = CurrentAccountRules.Summarize([invoice, payment], today);
        Assert.Equal(60_000m, paid.Balance);
        Assert.Equal(0m, paid.Overdue);
        Assert.Equal(60_000m, paid.Current);

        var reversed = CurrentAccountRules.Summarize([invoice, payment, Reversal(payment, today)], today);
        Assert.Equal(100_000m, reversed.Balance);
        Assert.Equal(100_000m, reversed.Current);

        // Thirty-one days later the invoice is one day overdue and the whole debt is in the first bucket.
        var later = CurrentAccountRules.Summarize([invoice, payment, Reversal(payment, today)], today.AddDays(31));
        Assert.Equal(100_000m, later.Overdue);
        Assert.Equal(100_000m, later.Aging.D0To30);
    }

    [Fact]
    public void ReversedDocuments_AreExcluded_AndAReversalAfterAsOfIsNotYetInForce()
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100m, new(2026, 8, 1), new(2026, 8, 15));
        var wrong = Fact(AccountMovementKind.Invoice, 70m, new(2026, 8, 2), new(2026, 8, 16));
        var reversalOfWrong = Reversal(wrong, new(2026, 8, 3));

        var summary = CurrentAccountRules.Summarize([invoice, wrong, reversalOfWrong], AsOf);
        Assert.Equal(100m, summary.Balance);
        Assert.Equal(100m, summary.Overdue);

        // As of a date before the reversal, the wrong invoice still stands.
        var before = CurrentAccountRules.Summarize([invoice, wrong, reversalOfWrong], new DateOnly(2026, 8, 2));
        Assert.Equal(170m, before.Balance);
    }

    [Fact]
    public void AnOverpayment_LeavesNegativeBalance_AndNothingOverdue()
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100m, new(2026, 8, 1), new(2026, 8, 15));
        var payment = Fact(AccountMovementKind.Payment, 150m, new(2026, 8, 20));

        var summary = CurrentAccountRules.Summarize([invoice, payment], AsOf);

        Assert.Equal(-50m, summary.Balance);
        Assert.Equal(0m, summary.Overdue);
        Assert.Equal(0m, summary.Current);
    }

    [Fact]
    public void CreditNotesAndDebitAdjustments_ReduceTheDebt_LikePayments()
    {
        var invoice = Fact(AccountMovementKind.Invoice, 100m, new(2026, 8, 1), new(2026, 8, 15));
        var creditNote = Fact(AccountMovementKind.CreditNote, 30m, new(2026, 8, 20));
        var adjustment = Fact(AccountMovementKind.Adjustment, 10m, new(2026, 8, 21), direction: AccountDirection.Debit);

        var summary = CurrentAccountRules.Summarize([invoice, creditNote, adjustment], AsOf);

        Assert.Equal(60m, summary.Balance);
        Assert.Equal(60m, summary.Overdue);
    }

    [Fact]
    public void OpeningBalanceAndDebitNotes_AreDebtDocumentsToo()
    {
        var opening = Fact(AccountMovementKind.OpeningBalance, 500m, new(2026, 1, 1), new(2026, 1, 31));
        var debitNote = Fact(AccountMovementKind.DebitNote, 20m, new(2026, 9, 1), new(2026, 9, 20));

        var summary = CurrentAccountRules.Summarize([opening, debitNote], AsOf);

        Assert.Equal(520m, summary.Balance);
        Assert.Equal(500m, summary.Aging.D90Plus);
        Assert.Equal(20m, summary.Aging.D0To30);
    }
}
