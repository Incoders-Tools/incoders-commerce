namespace Commerce.Domain.Pricing;

/// <summary>
/// One component of a <see cref="RateBreakdown"/>: the rate row as published, the amount its percentage was computed
/// ON (the base price for a <see cref="RateCalculationBase.Base"/> component, the running subtotal for a
/// <see cref="RateCalculationBase.Subtotal"/> one) and the amount it adds to the price.
/// </summary>
public sealed record RateBreakdownLine(
    string Code,
    string Label,
    decimal Percentage,
    RateCalculationBase CalculationBase,
    int Order,
    decimal CalculationAmount,
    decimal Amount);

/// <summary>
/// How a final list price is made from a base price (customer-price-lists T3): the base, every component in
/// <see cref="RateComponent.Order"/> and the final price. Not rounded, exactly like <see cref="RateComponentSet.Compose"/>
/// (rounding stays in <c>PricingResolutionService</c>, applied once to the unit net price).
/// </summary>
public sealed record RateBreakdown(decimal BasePrice, IReadOnlyList<RateBreakdownLine> Lines, decimal FinalPrice);
