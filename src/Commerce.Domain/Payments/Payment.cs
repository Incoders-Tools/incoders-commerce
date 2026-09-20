namespace Commerce.Domain.Payments;

/// <summary>
/// Aggregate root over one <see cref="PaymentSubject"/> (commerce-payments
/// design.md "Aggregate shape"). Ordered, append-only: <see cref="Append"/>
/// is the only mutator — no Update, no Remove. Nothing here stores a
/// balance; that is <c>SettlementCalculator.Fold</c>'s job (Unit 2), derived
/// on read from <see cref="Entries"/>.
/// </summary>
public sealed class Payment
{
    private readonly List<PaymentEntry> _entries = [];

    public PaymentSubject Subject { get; }
    public IReadOnlyList<PaymentEntry> Entries => _entries;

    public Payment(PaymentSubject subject)
    {
        Subject = subject;
    }

    public void Append(PaymentEntry entry) => _entries.Add(entry);
}
