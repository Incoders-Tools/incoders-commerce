namespace Commerce.Domain.Payments;

/// <summary>
/// The branch-durable payment effect, <c>SaleEffect</c>'s counterpart (same
/// record style; commerce-payments design.md File Changes). Guards mirror
/// <c>SaleEffect</c>'s: non-empty ids, positive amount. String-typed
/// <see cref="SubjectKind"/>/<see cref="EntryKind"/>/<see cref="Method"/>
/// mirror how <c>SaleEffect.SaleKind</c> is a plain string at the branch
/// boundary (SQLite has no enum type), converted to/from the domain enums at
/// the cloud projection boundary (<c>PaymentEffectApplier</c>, Unit 4).
/// </summary>
public sealed record PaymentEffect
{
    public Guid EntryId { get; }
    public Guid BranchId { get; }
    public string SubjectKind { get; }
    public Guid SubjectId { get; }
    public string Method { get; }
    public decimal Amount { get; }
    public string EntryKind { get; }
    public Guid? ReversesEntryId { get; }
    public DateTimeOffset OccurredAtUtc { get; }

    public PaymentEffect(
        Guid EntryId,
        Guid BranchId,
        string SubjectKind,
        Guid SubjectId,
        string Method,
        decimal Amount,
        string EntryKind,
        Guid? ReversesEntryId,
        DateTimeOffset OccurredAtUtc)
    {
        if (EntryId == Guid.Empty)
        {
            throw new ArgumentException("EntryId must not be empty.", nameof(EntryId));
        }

        if (BranchId == Guid.Empty)
        {
            throw new ArgumentException("BranchId must not be empty.", nameof(BranchId));
        }

        if (Amount <= 0)
        {
            throw new ArgumentException("Amount must be positive.", nameof(Amount));
        }

        this.EntryId = EntryId;
        this.BranchId = BranchId;
        this.SubjectKind = SubjectKind;
        this.SubjectId = SubjectId;
        this.Method = Method;
        this.Amount = Amount;
        this.EntryKind = EntryKind;
        this.ReversesEntryId = ReversesEntryId;
        this.OccurredAtUtc = OccurredAtUtc;
    }
}
