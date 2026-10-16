using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.11 (commerce-payments design.md File Changes:
/// "PaymentEffect.cs — the branch-durable effect, SaleEffect's counterpart,
/// same record style"): construction guards mirror <see cref="SaleEffect"/>'s
/// style — non-empty ids, positive amount.
/// </summary>
public sealed class PaymentEffectTests
{
    private static PaymentEffect NewEffect(
        Guid? entryId = null, Guid? branchId = null, decimal amount = 10m) =>
        new(
            EntryId: entryId ?? Guid.NewGuid(),
            BranchId: branchId ?? Guid.NewGuid(),
            SubjectKind: "Sale",
            SubjectId: Guid.NewGuid(),
            Method: "Cash",
            Amount: amount,
            EntryKind: "Payment",
            ReversesEntryId: null,
            OccurredAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Constructor_EmptyEntryId_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewEffect(entryId: Guid.Empty));
    }

    [Fact]
    public void Constructor_EmptyBranchId_Throws()
    {
        Assert.Throws<ArgumentException>(() => NewEffect(branchId: Guid.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_NonPositiveAmount_Throws(decimal amount)
    {
        Assert.Throws<ArgumentException>(() => NewEffect(amount: amount));
    }

    [Fact]
    public void Constructor_ValidValues_Succeeds()
    {
        var effect = NewEffect();
        Assert.Equal(10m, effect.Amount);
    }
}
