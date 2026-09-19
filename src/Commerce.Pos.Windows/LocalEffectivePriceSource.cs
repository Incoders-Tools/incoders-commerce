using Commerce.Application.Pricing;
using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// The POS half of the shared <see cref="IEffectivePriceSource"/> port
/// (commerce-pricing-engine design.md "`PricingResolutionService` contract
/// and location"): reads the replicated <c>price_replica</c> table via
/// <see cref="BranchSyncStore.GetEffectivePrice"/> instead of Postgres. The
/// exact same <see cref="PricingResolutionService"/> compiled method runs
/// online (<c>PostgresEffectivePriceSource</c>) and offline (this) — channel
/// parity is a structural property of sharing this port, not a convention.
/// </summary>
public sealed class LocalEffectivePriceSource : IEffectivePriceSource
{
    private readonly BranchSyncStore _store;

    public LocalEffectivePriceSource(BranchSyncStore store) => _store = store;

    public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
        Task.FromResult(_store.GetEffectivePrice(presentationId, effectiveOn));
}
