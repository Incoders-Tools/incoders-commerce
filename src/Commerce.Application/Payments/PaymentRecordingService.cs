using Commerce.Domain.Payments;

namespace Commerce.Application.Payments;

/// <summary>
/// Record / reverse / query settlement (commerce-payments design.md
/// "Interfaces / Contracts"). Maps the gateway's application-layer
/// <see cref="PaymentApprovalOutcome"/> onto the domain-layer
/// <see cref="PaymentApprovalState"/> and appends via
/// <see cref="Payment.Append"/>-equivalent semantics through
/// <see cref="IPaymentLedgerStore"/> — Approved/Declined append a history
/// entry; Unavailable appends nothing (Decision 2, fail-closed).
/// </summary>
public sealed class PaymentRecordingService
{
    private readonly IPaymentLedgerStore _store;
    private readonly IPaymentApprovalGateway _gateway;

    public PaymentRecordingService(IPaymentLedgerStore store, IPaymentApprovalGateway gateway)
    {
        _store = store;
        _gateway = gateway;
    }

    public async Task<PaymentEntry?> RecordAsync(
        PaymentSubject subject, Guid organizationId, PaymentMethod method, decimal amount, Guid actorId, Guid entryId, CancellationToken ct)
    {
        var outcome = await _gateway.Approve(subject.SubjectId, amount, method.ToString(), ct);

        if (outcome == PaymentApprovalOutcome.Unavailable)
        {
            // Fail-closed (Decision 2): no ledger entry is ever appended for
            // an unavailable outcome — never a silent success, never a
            // silent zero.
            return null;
        }

        var approvalState = outcome == PaymentApprovalOutcome.Approved
            ? PaymentApprovalState.Approved
            : PaymentApprovalState.Declined;

        var entry = new PaymentEntry(
            EntryId: entryId,
            OrganizationId: organizationId,
            Subject: subject,
            Kind: PaymentEntryKind.Payment,
            Method: method,
            Amount: amount,
            ApprovalState: approvalState,
            ReversesEntryId: null,
            ProviderReference: null,
            ActorId: actorId,
            RecordedAtUtc: DateTimeOffset.UtcNow);

        await _store.AppendAsync(entry, ct);
        return entry;
    }

    /// <summary>
    /// Appends a NEW <see cref="PaymentEntryKind.Reversal"/> entry referencing
    /// <paramref name="entryId"/>. The original entry is never mutated or
    /// removed — Partial Payments and Reversals Without History Mutation.
    /// </summary>
    public async Task<PaymentEntry> ReverseAsync(
        Guid entryId, PaymentSubject subject, Guid organizationId, Guid actorId, Guid reversalEntryId, CancellationToken ct)
    {
        var existing = await _store.GetEntriesAsync(subject, organizationId, ct);
        var original = existing.Single(e => e.EntryId == entryId);

        var reversal = new PaymentEntry(
            EntryId: reversalEntryId,
            OrganizationId: original.OrganizationId,
            Subject: subject,
            Kind: PaymentEntryKind.Reversal,
            Method: original.Method,
            Amount: original.Amount,
            ApprovalState: PaymentApprovalState.Approved,
            ReversesEntryId: entryId,
            ProviderReference: null,
            ActorId: actorId,
            RecordedAtUtc: DateTimeOffset.UtcNow);

        await _store.AppendAsync(reversal, ct);
        return reversal;
    }

    /// <summary>
    /// Composes <see cref="IPaymentLedgerStore.GetEntriesAsync"/> with
    /// <see cref="SettlementCalculator.Fold"/> — Settlement Query Surface Is
    /// Reporting-Only, Never a Gate.
    /// </summary>
    public async Task<Settlement> GetSettlementAsync(PaymentSubject subject, Guid organizationId, decimal target, CancellationToken ct)
    {
        var entries = await _store.GetEntriesAsync(subject, organizationId, ct);
        return SettlementCalculator.Fold(target, entries);
    }
}
