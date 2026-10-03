using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Pricing;

namespace Commerce.Cloud.Api.Pricing;

/// <summary>
/// One product whose price on <see cref="PriceListName"/> would fall below the same product's price on its floor list
/// (customer-price-lists, the floor rule). Both prices are FINAL prices (base composed with the list's rate set, rounded
/// to the cent) on the date being validated.
/// </summary>
public sealed record FloorViolation(
    Guid PriceListId,
    string PriceListName,
    Guid FloorPriceListId,
    string FloorPriceListName,
    Guid PresentationId,
    Guid ProductId,
    string ProductName,
    string PresentationName,
    decimal Price,
    decimal FloorPrice);

/// <summary>A product of a list on a date: its base price and the final price once the list's rate set is composed.</summary>
public sealed record PricedItem(
    Guid PresentationId, Guid ProductId, string ProductName, string PresentationName, decimal BasePrice, decimal FinalPrice);

/// <summary>
/// The floor rule itself, pure: a product priced on both lists must not be cheaper on the lower list than on its floor
/// list. A product that the floor list does not price has nothing to be compared with (a counter-only cut) and is never
/// a violation.
/// </summary>
public static class PriceFloorRule
{
    public static IReadOnlyList<FloorViolation> Find(
        PriceListRecord lower, PriceListRecord floor, IEnumerable<PricedItem> lowerItems, IEnumerable<PricedItem> floorItems)
    {
        var floorByPresentation = floorItems.ToDictionary(i => i.PresentationId);
        return
        [
            .. lowerItems
                .Where(i => floorByPresentation.TryGetValue(i.PresentationId, out var f) && i.FinalPrice < f.FinalPrice)
                .OrderBy(i => i.ProductName, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new FloorViolation(
                    lower.Id, lower.Name, floor.Id, floor.Name, i.PresentationId, i.ProductId, i.ProductName, i.PresentationName,
                    i.FinalPrice, floorByPresentation[i.PresentationId].FinalPrice)),
        ];
    }
}

/// <summary>
/// Validates a price or composition change against the floor rule BEFORE it is published (409 `price-below-floor`).
/// A change to a list L is checked in both directions: L against its own floor list, and every list whose floor is L
/// against L's new prices (raising a floor list can push the lists above it under their own floor). Validation is on the
/// change's effective date only: later-dated entries or sets of either list are not looked at.
/// </summary>
public sealed class PriceFloorValidator
{
    private readonly PostgresPriceListStore _priceLists;
    private readonly PostgresRateComponentStore _rateComponents;

    public PriceFloorValidator(PostgresPriceListStore priceLists, PostgresRateComponentStore rateComponents)
    {
        _priceLists = priceLists;
        _rateComponents = rateComponents;
    }

    /// <summary>
    /// A proposed change to an existing list: new base prices for some presentations (<paramref name="entryOverrides"/>)
    /// and/or a new rate component set (<paramref name="setOverride"/>), both effective on <paramref name="on"/>.
    /// </summary>
    public async Task<IReadOnlyList<FloorViolation>> CheckChangeAsync(
        CloudTenantScope scope, Guid priceListId, DateOnly on,
        IReadOnlyDictionary<Guid, decimal>? entryOverrides, RateComponentSet? setOverride, CancellationToken ct)
    {
        var lists = await _priceLists.ListPriceListsAsync(scope, ct);
        var target = lists.FirstOrDefault(l => l.Id == priceListId);
        if (target is null)
        {
            return [];
        }

        var violations = new List<FloorViolation>();
        List<PricedItem>? targetItems = null;

        if (target.FloorPriceListId is { } floorId && lists.FirstOrDefault(l => l.Id == floorId) is { } floor)
        {
            targetItems = await PriceAsync(scope, target.Id, on, entryOverrides, setOverride, ct);
            violations.AddRange(PriceFloorRule.Find(target, floor, targetItems, await PriceAsync(scope, floor.Id, on, null, null, ct)));
        }

        foreach (var dependent in lists.Where(l => l.FloorPriceListId == target.Id))
        {
            targetItems ??= await PriceAsync(scope, target.Id, on, entryOverrides, setOverride, ct);
            violations.AddRange(PriceFloorRule.Find(dependent, target, await PriceAsync(scope, dependent.Id, on, null, null, ct), targetItems));
        }

        return violations;
    }

    /// <summary>A list that is about to be created (a copy): its base prices and set against the floor it will have.</summary>
    public async Task<IReadOnlyList<FloorViolation>> CheckNewListAsync(
        CloudTenantScope scope, string name, Guid floorPriceListId, DateOnly on,
        IReadOnlyDictionary<Guid, decimal> entries, RateComponentSet? set, CancellationToken ct)
    {
        var floor = await _priceLists.FindPriceListAsync(scope, floorPriceListId, ct);
        if (floor is null)
        {
            return [];
        }

        var labels = await _priceLists.GetPresentationLabelsAsync(scope, [.. entries.Keys], ct);
        var items = entries
            .Where(e => labels.ContainsKey(e.Key))
            .Select(e => new PricedItem(e.Key, labels[e.Key].ProductId, labels[e.Key].ProductName, labels[e.Key].PresentationName,
                e.Value, Final(set, e.Value)))
            .ToList();
        var pseudo = new PriceListRecord(Guid.Empty, scope.OrganizationId, scope.BranchId ?? Guid.Empty, name, false, DateTimeOffset.UtcNow, Guid.Empty, floorPriceListId);
        return PriceFloorRule.Find(pseudo, floor, items, await PriceAsync(scope, floor.Id, on, null, null, ct));
    }

    /// <summary>Setting <paramref name="floorPriceListId"/> as the floor of a list: what it prices today must already respect it.</summary>
    public async Task<IReadOnlyList<FloorViolation>> CheckFloorAsync(
        CloudTenantScope scope, Guid priceListId, Guid floorPriceListId, DateOnly on, CancellationToken ct)
    {
        var list = await _priceLists.FindPriceListAsync(scope, priceListId, ct);
        var floor = await _priceLists.FindPriceListAsync(scope, floorPriceListId, ct);
        if (list is null || floor is null)
        {
            return [];
        }

        return PriceFloorRule.Find(
            list, floor, await PriceAsync(scope, list.Id, on, null, null, ct), await PriceAsync(scope, floor.Id, on, null, null, ct));
    }

    /// <summary>Would making <paramref name="floorPriceListId"/> the floor of <paramref name="priceListId"/> close a loop of floors?</summary>
    public async Task<bool> WouldCycleAsync(CloudTenantScope scope, Guid priceListId, Guid floorPriceListId, CancellationToken ct)
    {
        var byId = (await _priceLists.ListPriceListsAsync(scope, ct)).ToDictionary(l => l.Id);
        var seen = new HashSet<Guid>();
        for (Guid? current = floorPriceListId; current is { } id && seen.Add(id); current = byId.GetValueOrDefault(id)?.FloorPriceListId)
        {
            if (id == priceListId)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Row(Guid PresentationId, Guid ProductId, string ProductName, string PresentationName, decimal Base);

    private static decimal Final(RateComponentSet? set, decimal basePrice) => Money.Round2(set?.Compose(basePrice) ?? basePrice);

    /// <summary>The products of a list on a date with their final prices, optionally with proposed base prices and/or set.</summary>
    private async Task<List<PricedItem>> PriceAsync(
        CloudTenantScope scope, Guid priceListId, DateOnly on,
        IReadOnlyDictionary<Guid, decimal>? entryOverrides, RateComponentSet? setOverride, CancellationToken ct)
    {
        var set = setOverride ?? await _rateComponents.GetEffectiveSetAsync(scope, priceListId, on, ct);
        var items = (await _priceLists.ListItemsAsOfAsync(scope, priceListId, on, ct))
            .Select(i => new Row(i.PresentationId, i.ProductId, i.ProductName, i.PresentationName, i.UnitPrice))
            .ToDictionary(i => i.PresentationId);

        if (entryOverrides is { Count: > 0 })
        {
            var unknown = entryOverrides.Keys.Where(k => !items.ContainsKey(k)).ToArray();
            var labels = await _priceLists.GetPresentationLabelsAsync(scope, unknown, ct);
            foreach (var (presentationId, price) in entryOverrides)
            {
                if (items.TryGetValue(presentationId, out var existing))
                {
                    items[presentationId] = existing with { Base = price };
                }
                else if (labels.TryGetValue(presentationId, out var label))
                {
                    items[presentationId] = new Row(presentationId, label.ProductId, label.ProductName, label.PresentationName, price);
                }
            }
        }

        return [.. items.Values.Select(i => new PricedItem(i.PresentationId, i.ProductId, i.ProductName, i.PresentationName, i.Base, Final(set, i.Base)))];
    }
}
