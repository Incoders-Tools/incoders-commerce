using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api;

/// <summary>
/// Receives branch-originated synchronization operations, applies them
/// idempotently under the claim-derived tenant scope, and acknowledges only
/// after a durable commit (ADR-003).
/// </summary>
public sealed class CloudSyncReceiver
{
    private readonly ICloudInboxStore _store;

    public CloudSyncReceiver(ICloudInboxStore store) => _store = store;

    public InboundApplyResult Receive(CloudTenantScope scope, SyncEnvelope envelope) =>
        _store.TryApplyInbound(scope, envelope);

    public bool Acknowledge(CloudTenantScope scope, Guid operationId) =>
        _store.Acknowledge(scope, operationId);
}
