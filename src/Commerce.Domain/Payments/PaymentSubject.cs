namespace Commerce.Domain.Payments;

/// <summary>
/// What a payment settles against: an <see cref="Order"/> (web/managed) or a
/// POS <c>Sale</c> — never embedded in either (ADR-011; commerce-payments
/// design.md "Payment as a Separate Aggregate from Order").
/// </summary>
public enum PaymentSubjectKind
{
    Order,
    Sale
}

/// <summary>
/// The (kind, id) pair a <see cref="Payment"/> ledger settles.
/// <see cref="SubjectId"/> must be non-empty.
/// </summary>
public sealed record PaymentSubject
{
    public PaymentSubjectKind Kind { get; }
    public Guid SubjectId { get; }

    public PaymentSubject(PaymentSubjectKind kind, Guid subjectId)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("SubjectId must not be empty.", nameof(subjectId));
        }

        Kind = kind;
        SubjectId = subjectId;
    }
}
