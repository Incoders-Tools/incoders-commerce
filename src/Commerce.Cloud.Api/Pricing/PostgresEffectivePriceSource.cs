using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;

namespace Commerce.Cloud.Api.Pricing;

/// <summary>
/// The cloud half of the shared <see cref="IEffectivePriceSource"/> port
/// (commerce-pricing-engine design.md "`PricingResolutionService` contract
/// and location"), wrapping <see cref="PostgresPriceListStore.GetEffectiveAsync"/>
/// — the org's default price list, resolved once per scoped instance rather
/// than re-derived per call, since <see cref="IEffectivePriceSource"/>
/// deliberately carries no organization or price-list parameter.
/// </summary>
public sealed class PostgresEffectivePriceSource : IEffectivePriceSource
{
    private readonly PostgresPriceListStore _priceListStore;
    private readonly CloudTenantScope _scope;
    private readonly Guid _priceListId;

    public PostgresEffectivePriceSource(PostgresPriceListStore priceListStore, CloudTenantScope scope, Guid priceListId)
    {
        _priceListStore = priceListStore;
        _scope = scope;
        _priceListId = priceListId;
    }

    public async Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
    {
        var entry = await _priceListStore.GetEffectiveAsync(_scope, _priceListId, presentationId, effectiveOn, ct);
        return entry?.UnitPrice;
    }
}
