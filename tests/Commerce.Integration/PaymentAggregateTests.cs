using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.8 (commerce-payments design.md "Aggregate shape"):
/// <see cref="Payment"/> is append-only — <c>Append</c> is the only mutator,
/// there is no Update/Remove member, and a reversal never hides the original
/// entry from history.
/// </summary>
public sealed class PaymentAggregateTests
{
    private static PaymentEntry NewEntry(
        PaymentSubject subject, PaymentEntryKind kind = PaymentEntryKind.Payment, Guid? reversesEntryId = null) =>
        new(
            EntryId: Guid.NewGuid(),
            OrganizationId: Guid.NewGuid(),
            Subject: subject,
            Kind: kind,
            Method: PaymentMethod.Cash,
            Amount: 50m,
            ApprovalState: PaymentApprovalState.Approved,
            ReversesEntryId: reversesEntryId,
            ProviderReference: null,
            ActorId: Guid.NewGuid(),
            RecordedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Append_AddsEntry_NoUpdateOrRemoveMemberExists()
    {
        var subject = new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid());
        var payment = new Payment(subject);
        var entry = NewEntry(subject);

        payment.Append(entry);

        Assert.Single(payment.Entries);
        Assert.Contains(entry, payment.Entries);

        // No Update/Remove member exists on Payment — reflection guard.
        var methodNames = typeof(Payment).GetMethods().Select(m => m.Name);
        Assert.DoesNotContain("Update", methodNames);
        Assert.DoesNotContain("Remove", methodNames);
    }

    [Fact]
    public void Append_Reversal_KeepsOriginalAndReversalBothVisible()
    {
        var subject = new PaymentSubject(PaymentSubjectKind.Order, Guid.NewGuid());
        var payment = new Payment(subject);
        var original = NewEntry(subject);
        payment.Append(original);

        var reversal = NewEntry(subject, kind: PaymentEntryKind.Reversal, reversesEntryId: original.EntryId);
        payment.Append(reversal);

        Assert.Equal(2, payment.Entries.Count);
        Assert.Contains(original, payment.Entries);
        Assert.Contains(reversal, payment.Entries);
        Assert.Equal(original, payment.Entries[0]);
        Assert.Equal(reversal, payment.Entries[1]);
    }
}
