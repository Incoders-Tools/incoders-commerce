using Commerce.Domain.Payments;

namespace Commerce.Application.Payments;

/// <summary>
/// Append-only ledger store port (commerce-payments design.md "Interfaces /
/// Contracts"), mirroring the existing store-abstraction pattern in this repo
/// (e.g. <c>IEmailSender</c>). Implemented by <c>PostgresPaymentStore</c>
/// (Unit 4); backed by an in-memory fake for this unit's tests.
/// </summary>
public interface IPaymentLedgerStore
{
    Task AppendAsync(PaymentEntry entry, CancellationToken ct);

    /// <summary>
    /// <paramref name="organizationId"/> is required (not derivable from
    /// <paramref name="subject"/> alone) so a real tenant-scoped
    /// implementation (<c>PostgresPaymentStore</c>) can set the RLS session
    /// scope before reading — mirrors every other scoped store in this repo
    /// (e.g. <c>CloudOrderStore.Find(scope, orderId)</c>).
    /// </summary>
    Task<IReadOnlyList<PaymentEntry>> GetEntriesAsync(PaymentSubject subject, Guid organizationId, CancellationToken ct);
}
