using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.5 (commerce-payments design.md "Aggregate shape"):
/// <see cref="PaymentEntry"/> invariants. An entry rejects a non-positive
/// amount; a <see cref="PaymentEntryKind.Reversal"/> entry requires
/// <c>ReversesEntryId</c>; a <see cref="PaymentEntryKind.Payment"/> entry must
/// NOT carry one.
/// </summary>
public sealed class PaymentLedgerTests
{
    private static PaymentEntry NewEntry(
        PaymentEntryKind kind = PaymentEntryKind.Payment,
        decimal amount = 100m,
        Guid? reversesEntryId = null,
        PaymentApprovalState approvalState = PaymentApprovalState.Approved) =>
        new(
            EntryId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            Subject: new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid()),
            Kind: kind,
            Method: PaymentMethod.Cash,
            Amount: amount,
            ApprovalState: approvalState,
            ReversesEntryId: reversesEntryId,
            ProviderReference: null,
            ActorId: Guid.NewGuid(),
            RecordedAtUtc: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveAmount_Throws(decimal amount)
    {
        Assert.Throws<ArgumentException>(() => NewEntry(amount: amount));
    }

    [Fact]
    public void Constructor_ReversalWithoutReversesEntryId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            NewEntry(kind: PaymentEntryKind.Reversal, reversesEntryId: null));
    }

    [Fact]
    public void Constructor_PaymentWithReversesEntryId_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            NewEntry(kind: PaymentEntryKind.Payment, reversesEntryId: Guid.NewGuid()));
    }

    [Fact]
    public void Constructor_ReversalWithReversesEntryId_Succeeds()
    {
        var entry = NewEntry(kind: PaymentEntryKind.Reversal, reversesEntryId: Guid.NewGuid());

        Assert.Equal(PaymentEntryKind.Reversal, entry.Kind);
        Assert.NotNull(entry.ReversesEntryId);
    }
}
