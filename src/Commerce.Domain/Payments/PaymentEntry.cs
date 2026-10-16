namespace Commerce.Domain.Payments;

public enum PaymentEntryKind
{
    Payment,
    Reversal
}

/// <summary>
/// Immutable ledger entry (commerce-payments design.md "Aggregate shape" /
/// "Interfaces / Contracts"). Append-only: there is no setter, no Update, no
/// Remove — a reversal is a NEW entry referencing the reversed one, never a
/// mutation of it. Guards: <see cref="Amount"/> must be positive; a
/// <see cref="PaymentEntryKind.Reversal"/> requires
/// <see cref="ReversesEntryId"/>; a <see cref="PaymentEntryKind.Payment"/>
/// must not carry one.
/// </summary>
public sealed record PaymentEntry
{
    public Guid EntryId { get; }
    public Guid OrganizationId { get; }
    public PaymentSubject Subject { get; }
    public PaymentEntryKind Kind { get; }
    public PaymentMethod Method { get; }
    public decimal Amount { get; }
    public PaymentApprovalState ApprovalState { get; }
    public Guid? ReversesEntryId { get; }
    public string? ProviderReference { get; }
    public Guid ActorId { get; }
    public DateTimeOffset RecordedAtUtc { get; }

    public PaymentEntry(
        Guid EntryId,
        Guid OrganizationId,
        PaymentSubject Subject,
        PaymentEntryKind Kind,
        PaymentMethod Method,
        decimal Amount,
        PaymentApprovalState ApprovalState,
        Guid? ReversesEntryId,
        string? ProviderReference,
        Guid ActorId,
        DateTimeOffset RecordedAtUtc)
    {
        if (Amount <= 0)
        {
            throw new ArgumentException("Amount must be positive.", nameof(Amount));
        }

        if (Kind == PaymentEntryKind.Reversal && ReversesEntryId is null)
        {
            throw new ArgumentException("A Reversal entry requires ReversesEntryId.", nameof(ReversesEntryId));
        }

        if (Kind == PaymentEntryKind.Payment && ReversesEntryId is not null)
        {
            throw new ArgumentException("A Payment entry must not carry ReversesEntryId.", nameof(ReversesEntryId));
        }

        this.EntryId = EntryId;
        this.OrganizationId = OrganizationId;
        this.Subject = Subject;
        this.Kind = Kind;
        this.Method = Method;
        this.Amount = Amount;
        this.ApprovalState = ApprovalState;
        this.ReversesEntryId = ReversesEntryId;
        this.ProviderReference = ProviderReference;
        this.ActorId = ActorId;
        this.RecordedAtUtc = RecordedAtUtc;
    }
}
