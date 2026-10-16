using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 1 task 1.3 (commerce-payments design.md "Payment as a Separate
/// Aggregate from Order"): <see cref="PaymentSubject"/> rejects an empty
/// <c>SubjectId</c> and covers both <see cref="PaymentSubjectKind.Order"/> and
/// <see cref="PaymentSubjectKind.Sale"/>.
/// </summary>
public sealed class PaymentSubjectTests
{
    [Fact]
    public void Constructor_EmptySubjectId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PaymentSubject(PaymentSubjectKind.Order, Guid.Empty));
    }

    [Theory]
    [InlineData(PaymentSubjectKind.Order)]
    [InlineData(PaymentSubjectKind.Sale)]
    public void Constructor_NonEmptySubjectId_Succeeds(PaymentSubjectKind kind)
    {
        var subjectId = Guid.NewGuid();
        var subject = new PaymentSubject(kind, subjectId);

        Assert.Equal(kind, subject.Kind);
        Assert.Equal(subjectId, subject.SubjectId);
    }
}
