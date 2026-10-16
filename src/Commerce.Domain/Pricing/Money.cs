namespace Commerce.Domain.Pricing;

/// <summary>
/// The ONE rounding policy for the whole change (commerce-pricing-engine
/// design.md "Rounding policy (one, server-side)"): 2 decimals,
/// <see cref="MidpointRounding.AwayFromZero"/> — correct for retail money,
/// unlike .NET's default banker's rounding. Applied exactly TWICE by
/// <c>PricingResolutionService</c> (Work Unit 3): once for the unit net
/// price, once for the line total. The sale/order total is the plain sum of
/// already-rounded line totals — never re-rounded, never recomputed from
/// unit prices. <c>double</c> appears nowhere in this change; money is
/// <c>decimal</c> end to end (`numeric(12,2)` at rest).
/// </summary>
public static class Money
{
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
