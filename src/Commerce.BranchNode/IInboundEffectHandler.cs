using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>
/// The materialization seam (commerce-sync-ownership design.md "Materialization
/// contract"): a registry, not a framework — two entries, no discovery, no
/// reflection. Invoked INSIDE <see cref="BranchSyncStore.ApplyInbound"/>'s
/// existing transaction, so de-duplication (the `inbox` insert) and
/// materialization succeed or fail together by construction (Requirement:
/// Inbound Materialization Contract).
/// </summary>
public interface IInboundEffectHandler
{
    string PayloadKind { get; }

    void Apply(SyncEnvelope envelope, SqliteTransaction transaction);
}
