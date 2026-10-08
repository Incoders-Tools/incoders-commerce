namespace Commerce.Application.Pricing;

/// <summary>
/// customer-price-lists T6: the two resolution ports of ONE price list, bound together with its id so a resolved price can
/// say which list priced it. The id is identity only; the ports stay free of any channel or caller parameter (ADR-010).
/// </summary>
public sealed record PriceListPorts(
    Guid PriceListId,
    IEffectivePriceSource PriceSource,
    IEffectiveRateComponentSource? RateComponentSource = null);
