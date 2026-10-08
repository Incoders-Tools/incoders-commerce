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
///
/// R4-per-line-set-fetch: an instance also memoizes the set PER DATE. The
/// effective set is a function of exactly (scope, list, date), and the first
/// two are frozen at construction, so a second lookup for the same date cannot
/// return anything different. Without this an N-line order submission made N
/// independent connection acquisitions, each with its own transaction, its own
/// `set_config` and up to two header queries, to read one identical row N
/// times.
///
/// The cache is keyed by date rather than simply held as one value, so the
/// effective-dating contract (spec "Composition Uses The Rates Effective On The
/// Resolution Date") is untouched: two different dates are still two different
/// reads. Nothing moves out of `PricingResolutionService` — resolution still
/// asks the port once per line and the port is still the only authority on
/// which set applies (spec "Composition Is Part Of The Single Resolution
/// Authority").
///
/// SCOPE OF THE CACHE: one instance, which is constructed per submission and
/// discarded with it. It is deliberately NOT a process-wide or store-level
/// cache — a published set would then go on composing the old price for
/// whatever the eviction policy said, and this change ships no eviction policy
/// because it needs none. Not thread-safe, matching the single-threaded
/// resolution loop it serves.
/// </summary>
public sealed class PostgresRateComponentSource : IEffectiveRateComponentSource
{
    private readonly PostgresRateComponentStore _rateComponentStore;
    private readonly CloudTenantScope _scope;
    private readonly Guid _priceListId;
    private readonly Dictionary<DateOnly, RateComponentSet?> _byDate = [];

    public PostgresRateComponentSource(
        PostgresRateComponentStore rateComponentStore, CloudTenantScope scope, Guid priceListId)
    {
        _rateComponentStore = rateComponentStore;
        _scope = scope;
        _priceListId = priceListId;
    }

    public async Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct)
    {
        // `TryGetValue`, not `?? await`: `null` is a REAL answer here ("no set
        // effective for this date", composed as the identity), and a
        // null-coalescing cache would re-query for it on every single line —
        // precisely the order that costs the most lookups today.
        if (_byDate.TryGetValue(effectiveOn, out var cached))
        {
            return cached;
        }

        var set = await _rateComponentStore.GetEffectiveSetAsync(_scope, _priceListId, effectiveOn, ct);
        _byDate[effectiveOn] = set;
        return set;
    }
}
