using Commerce.Cloud.Api.Payments;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api;

/// <summary>
/// Receives branch-originated synchronization operations, applies them
/// idempotently under the claim-derived tenant scope, and acknowledges only
/// after a durable commit (ADR-003).
///
/// Dispatches by <see cref="SyncEnvelope.PayloadKind"/> (commerce-payments
/// design.md "Payment-Effect Synchronization Path Parallel to the Sale
/// Outbox"): a <c>"PaymentRecorded"</c> envelope routes to
/// <see cref="PaymentEffectApplier"/>, which projects into `payment_entries`
/// in the same transaction as the `sync_inbox` insert. Every OTHER
/// payload kind (sale envelopes included) is UNCHANGED — routed to
/// <see cref="ICloudInboxStore.TryApplyInbound"/> exactly as before this
/// change, byte-identical.
/// </summary>
public sealed class CloudSyncReceiver
{
    private const string PaymentRecordedPayloadKind = "PaymentRecorded";

    private readonly ICloudInboxStore _store;
    private readonly PaymentEffectApplier? _paymentEffectApplier;

    public CloudSyncReceiver(ICloudInboxStore store, PaymentEffectApplier? paymentEffectApplier = null)
    {
        _store = store;
        _paymentEffectApplier = paymentEffectApplier;
    }

    public InboundApplyResult Receive(CloudTenantScope scope, SyncEnvelope envelope)
    {
        if (envelope.PayloadKind == PaymentRecordedPayloadKind && _paymentEffectApplier is not null)
        {
            return _paymentEffectApplier.ApplyAsync(scope, envelope, CancellationToken.None).GetAwaiter().GetResult();
        }

        // Unchanged path: every existing (sale) envelope kind dispatches
        // exactly as before this change.
        return _store.TryApplyInbound(scope, envelope);
    }

    public bool Acknowledge(CloudTenantScope scope, Guid operationId) =>
        _store.Acknowledge(scope, operationId);
}
