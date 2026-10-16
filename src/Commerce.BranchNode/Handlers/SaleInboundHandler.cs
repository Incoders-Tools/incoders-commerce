using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode.Handlers;

/// <summary>
/// Task 3.7: the branch-authored "sale" kind is dedup-only inbound — a
/// branch never receives its own sale back as an inbound envelope in this
/// design, so materialization here is intentionally a no-op, STATED
/// explicitly (design.md File Changes) rather than left implied by a missing
/// registry entry, which would instead roll back as `UnknownKind`.
/// </summary>
public sealed class SaleInboundHandler : IInboundEffectHandler
{
    public string PayloadKind => "sale";

    public void Apply(SyncEnvelope envelope, SqliteTransaction transaction)
    {
        // Intentionally no-op: dedup (the caller's `inbox` insert) is the
        // entire contract for this kind inbound.
    }
}
