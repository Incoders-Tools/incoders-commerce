using Commerce.Application.Payments;
using Commerce.Domain.Ordering;
using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 2 tasks 2.1/2.3/2.4 (commerce-payments design.md "Where the
/// arithmetic lives" / "Rounding / allocation policy"): the ONE fold that
/// derives settlement from frozen target + ordered entries, and the
/// largest-remainder report allocation — a separate, never-consulted-by-Fold
/// path.
/// </summary>
public sealed class SettlementCalculatorTests
{
    private static PaymentEntry NewEntry(
        decimal amount, PaymentEntryKind kind = PaymentEntryKind.Payment, Guid? reversesEntryId = null) =>
        new(
            EntryId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            Subject: new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid()),
            Kind: kind,
            Method: PaymentMethod.Cash,
            Amount: amount,
            ApprovalState: PaymentApprovalState.Approved,
            ReversesEntryId: reversesEntryId,
            ProviderReference: null,
            ActorId: Guid.NewGuid(),
            RecordedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Fold_TwoPartialPaymentsThenReversalOfOne_YieldsCorrectOutstanding_AndKeepsAllEntries()
    {
        var first = NewEntry(30m);
        var second = NewEntry(40m);
        var reversal = NewEntry(30m, PaymentEntryKind.Reversal, reversesEntryId: first.EntryId);

        var entries = new List<PaymentEntry> { first, second, reversal };

        var settlement = SettlementCalculator.Fold(target: 100m, entries: entries);

        // first (+30) + second (+40) - reversal-of-first (-30) = 40 settled.
        Assert.Equal(40m, settlement.Settled);
        Assert.Equal(60m, settlement.Outstanding);
        Assert.False(settlement.IsSettled);

        // Every entry, including the reversed one, is still present in the
        // input list passed to Fold — Fold never mutates or filters it.
        Assert.Equal(3, entries.Count);
        Assert.Contains(first, entries);
    }

    [Fact]
    public void Fold_TwoPartialPayments_ReduceOutstandingCorrectly()
    {
        var entries = new List<PaymentEntry> { NewEntry(25m), NewEntry(25m) };

        var settlement = SettlementCalculator.Fold(target: 100m, entries: entries);

        Assert.Equal(50m, settlement.Settled);
        Assert.Equal(50m, settlement.Outstanding);
        Assert.False(settlement.IsSettled);
    }

    [Fact]
    public void Fold_FullyPaid_IsSettledTrue()
    {
        var entries = new List<PaymentEntry> { NewEntry(100m) };

        var settlement = SettlementCalculator.Fold(target: 100m, entries: entries);

        Assert.Equal(100m, settlement.Settled);
        Assert.Equal(0m, settlement.Outstanding);
        Assert.True(settlement.IsSettled);
    }

    [Fact]
    public void FulfilmentAndSettlement_AreIndependentlyQueryable_DeliveredAndUnsettled()
    {
        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, Guid.NewGuid(), guestContact: null,
            Guid.NewGuid(), Array.Empty<OrderLineSnapshot>(), DateTimeOffset.UtcNow);
        order.MarkDestinationConfirmed();

        var settlement = SettlementCalculator.Fold(target: 100m, entries: []);

        Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, order.Status);
        Assert.False(settlement.IsSettled);
    }

    [Fact]
    public void FulfilmentAndSettlement_AreIndependentlyQueryable_PaidAndUndelivered()
    {
        var order = new Order(
            Guid.NewGuid(), Guid.NewGuid(), OrderOrigin.RegisteredCustomer, Guid.NewGuid(), guestContact: null,
            Guid.NewGuid(), Array.Empty<OrderLineSnapshot>(), DateTimeOffset.UtcNow);

        var settlement = SettlementCalculator.Fold(target: 100m, entries: [NewEntry(100m)]);

        Assert.Equal(OrderDeliveryStatus.PendingDestination, order.Status);
        Assert.True(settlement.IsSettled);
    }

    [Fact]
    public void AllocateForReport_NonDividingCase_SumsExactlyToPaymentAmount()
    {
        var lineTotals = new List<decimal> { 33.33m, 33.33m, 33.34m };

        var allocations = SettlementCalculator.AllocateForReport(amount: 100.00m, frozenLineTotals: lineTotals);

        Assert.Equal(100.00m, allocations.Sum());
        Assert.Equal(lineTotals.Count, allocations.Count);
    }

    [Fact]
    public void AllocateForReport_TiesBrokenByAscendingLineIndex_SumsExactly()
    {
        var lineTotals = new List<decimal> { 10m, 10m, 10m };

        var allocations = SettlementCalculator.AllocateForReport(amount: 10.00m, frozenLineTotals: lineTotals);

        Assert.Equal(10.00m, allocations.Sum());
    }
}
