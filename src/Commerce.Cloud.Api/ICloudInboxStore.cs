using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api;

/// <summary>
/// Port extracted from the original <see cref="CloudInboxStore"/> in a
/// behavior-preserving refactor (Unit 2, design.md "Interfaces / Contracts").
/// Every method takes a <see cref="CloudTenantScope"/> derived from
/// authenticated claims and NEVER accepts a raw organization ID from the
/// caller for row access. <see cref="CloudInboxStore"/> remains the in-memory
/// RLS-equivalent implementation used by the existing 62 xUnit tests;
/// <see cref="Persistence.PostgresCloudInboxStore"/> is the real
/// Npgsql-backed production implementation.
/// </summary>
public interface ICloudInboxStore
{
    /// <param name="installationId">The installation of the calling device credential (a claim, never part of the envelope), or null
    /// when unknown. Used to verify the human sale number the terminal claims; ingestion never depends on it.</param>
    InboundApplyResult TryApplyInbound(CloudTenantScope scope, SyncEnvelope envelope, Guid? installationId = null);

    bool Acknowledge(CloudTenantScope scope, Guid operationId);

    SyncOperationStatus? GetStatus(CloudTenantScope scope, Guid operationId);

    IReadOnlyList<SyncEnvelope> GetInboxFor(CloudTenantScope scope);
}
