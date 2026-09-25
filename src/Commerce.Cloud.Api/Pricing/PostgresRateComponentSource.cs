using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;

namespace Commerce.Cloud.Api.Pricing;

/// <summary>
/// The cloud half of the <see cref="IEffectiveRateComponentSource"/> port
/// (commerce-price-composition design.md "Resolution placement"), the exact
/// counterpart of <see cref="PostgresEffectivePriceSource"/>: it wraps
/// <see cref="PostgresRateComponentStore.GetEffectiveSetAsync"/> and binds the
/// tenant scope and the price list ONCE per scoped instance, because the port
/// deliberately carries neither.
///
/// All the selection rules — the list's own latest set at or before the date,
/// falling back to the organization's default set, all-or-nothing at the set
/// level, `null` for zero sets anywhere — live in the store's single SQL pair
/// and are NOT re-implemented here. This type exists only to bind the scope and
/// the list, so resolution reads the entry and the components for the same list
/// by construction rather than by agreement between two call sites.
/// </summary>
public sealed class PostgresRateComponentSource : IEffectiveRateComponentSource
{
    private readonly PostgresRateComponentStore _rateComponentStore;
    private readonly CloudTenantScope _scope;
    private readonly Guid _priceListId;

    public PostgresRateComponentSource(
        PostgresRateComponentStore rateComponentStore, CloudTenantScope scope, Guid priceListId)
    {
        _rateComponentStore = rateComponentStore;
        _scope = scope;
        _priceListId = priceListId;
    }

    public Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct) =>
        _rateComponentStore.GetEffectiveSetAsync(_scope, _priceListId, effectiveOn, ct);
}
