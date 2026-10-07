using System.Globalization;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Pricing;

namespace Commerce.Pos.Windows;

/// <summary>
/// customer-price-lists T4: the <see cref="IEffectivePriceSource"/> port of ONE price list, read from the branch replica.
/// The list is bound by the instance (like the cloud's), so the base price and the composition are read for the same list
/// by construction.
/// </summary>
public sealed class ReplicaListPriceSource : IEffectivePriceSource
{
    private readonly BranchSyncStore _store;
    private readonly Guid _priceListId;

    public ReplicaListPriceSource(BranchSyncStore store, Guid priceListId)
    {
        _store = store;
        _priceListId = priceListId;
    }

    public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
        Task.FromResult(_store.GetEffectivePrice(_priceListId, presentationId, effectiveOn));
}

/// <summary>
/// The <see cref="IEffectiveRateComponentSource"/> port of ONE price list from the branch replica: the list's own set, else
/// the organization default, else none (which composes to the base price) — the cloud's rule.
/// </summary>
public sealed class ReplicaListRateComponentSource : IEffectiveRateComponentSource
{
    private readonly BranchSyncStore _store;
    private readonly Guid _priceListId;

    public ReplicaListRateComponentSource(BranchSyncStore store, Guid priceListId)
    {
        _store = store;
        _priceListId = priceListId;
    }

    public Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct) =>
        Task.FromResult(_store.GetEffectiveRateSet(_priceListId, effectiveOn));
}

/// <summary>
/// How one buyer is priced at the POS: the <see cref="PricingResolutionService"/> bound to the buyer's list, the list name
/// (null on the legacy single-list path), whether the customer's own list was missing from the replica and the name of the
/// default list that prices what the buyer's list does not (null when the buyer is already priced from it), and the
/// customer's own discount percentage, applied by the service after the list composition (null: none).
/// </summary>
public sealed record BuyerPricing(
    PricingResolutionService Service, string? PriceListName, bool CustomerListUnavailable, string? FallbackListName = null,
    decimal? CustomerDiscountPercent = null)
{
    /// <summary>
    /// What the operator reads about the customer's discount ("Descuento del cliente: 10 % sobre los precios de la lista
    /// Reparto..."), or null when the buyer has none.
    /// </summary>
    public string? CustomerDiscountNote => CustomerDiscountPercent is { } percent
        ? $"Descuento del cliente: {PercentText(percent)} sobre los precios de {(PriceListName is null ? "lista" : $"la lista {PriceListName}")}. " +
          "Los precios de los productos y de la venta ya lo incluyen."
        : null;

    /// <summary>"10 %" ("12,5 %" in a comma culture): the short form of the customer's discount, or null.</summary>
    public string? CustomerDiscountShortText => CustomerDiscountPercent is { } percent ? $"Desc. cliente {PercentText(percent)}" : null;

    private static string PercentText(decimal percent) => $"{percent.ToString("0.##", CultureInfo.CurrentCulture)} %";

    /// <summary>The note a line carries when it was priced by the fallback list: its name, or null for a line priced by the buyer's own list.</summary>
    public string? FallbackNoteFor(PriceResolutionOutcome.Resolved resolved) => resolved.FellBack ? FallbackListName : null;

    /// <summary>"Lista: Mostrador", with a note when the customer's own list is not in this branch's replica; null on the legacy path.</summary>
    public string? Label => PriceListName is null
        ? null
        : CustomerListUnavailable
            ? $"Lista: {PriceListName} (la lista del cliente no está disponible en esta sucursal)"
            : $"Lista: {PriceListName}";
}

/// <summary>
/// Chooses the list for a buyer and binds the shared <see cref="PricingResolutionService"/> to it, with the same
/// <see cref="BuyerPriceListSelector"/> the cloud uses: walk-in -> the branch default list; customer -> its list, else the
/// organization default customer list, else the branch default; a list absent from the replica is skipped. When the branch
/// never synced price lists (or has no default), the single legacy list of `price_replica` prices the sale, as before.
/// </summary>
public sealed class BuyerPricingFactory
{
    private readonly BranchSyncStore _store;
    private readonly PricingResolutionService _legacy;

    public BuyerPricingFactory(BranchSyncStore store, PricingResolutionService legacy)
    {
        _store = store;
        _legacy = legacy;
    }

    private PriceListPorts PortsOf(Guid listId) =>
        new(listId, new ReplicaListPriceSource(_store, listId), new ReplicaListRateComponentSource(_store, listId));

    public BuyerPricing For(Guid? customerId)
    {
        // The customer's own discount applies on whichever list prices the sale (the legacy one included), as in the cloud.
        var discount = customerId is { } buyer && _store.GetCustomerDiscountPercentage(buyer) is { } percent and > 0m ? percent : (decimal?)null;
        var lists = _store.ListPriceLists();
        var defaultListId = lists.FirstOrDefault(l => l.IsDefault)?.Id;
        var customerListId = customerId is { } id ? _store.GetCustomerPriceListId(id) : null;

        var selected = BuyerPriceListSelector.Select(
            isCustomer: customerId is not null,
            customerListId,
            _store.GetOrganizationDefaultCustomerPriceListId(),
            defaultListId,
            isAvailable: candidate => lists.Any(l => l.Id == candidate));
        if (selected is not { } listId || lists.FirstOrDefault(l => l.Id == listId) is not { } list)
        {
            return new BuyerPricing(_legacy, PriceListName: null, CustomerListUnavailable: false, CustomerDiscountPercent: discount);
        }

        // customer-price-lists T6: what the buyer's list does not price, the branch default list does (its own composition).
        var fallback = defaultListId is { } defaultId && defaultId != listId ? PortsOf(defaultId) : null;
        var customerListMissing = customerListId is { } own && lists.All(l => l.Id != own);
        return new BuyerPricing(
            new PricingResolutionService(PortsOf(listId), fallback),
            list.Name,
            customerListMissing,
            fallback is null ? null : lists.First(l => l.Id == fallback.PriceListId).Name,
            discount);
    }
}
