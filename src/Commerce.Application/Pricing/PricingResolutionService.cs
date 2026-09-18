using Commerce.Domain.Pricing;

namespace Commerce.Application.Pricing;

/// <summary>
/// The ADR-010 engine (commerce-pricing-engine design.md
/// "`PricingResolutionService` contract and location") — the ONE place a
/// price is computed for a `(customer context, presentation, quantity)`
/// tuple. Lives in `Commerce.Application` so both the cloud API and the POS
/// (via their existing `ProjectReference`) compile against and run the
/// exact same compiled method: channel independence is a structural
/// property of this signature (no channel/caller-identity parameter exists
/// anywhere), not merely a tested convention.
/// </summary>
public sealed class PricingResolutionService
{
    private readonly IEffectivePriceSource _priceSource;

    public PricingResolutionService(IEffectivePriceSource priceSource) => _priceSource = priceSource;

    /// <summary>
    /// `discountPercentage == null` => GUEST: the official list price, no
    /// discount. `discountPercentage != null` => REGISTERED: the customer's
    /// own `DiscountPercentage`. Rounding (`Money.Round2`, AwayFromZero) is
    /// applied EXACTLY TWICE: once for the unit-net price, once for the line
    /// total — never a third time at any higher level.
    /// </summary>
    public async Task<PriceResolutionOutcome> ResolveAsync(
        Guid presentationId, decimal quantity, decimal? discountPercentage,
        DateOnly effectiveOn, CancellationToken ct)
    {
        var unitPrice = await _priceSource.GetUnitPriceAsync(presentationId, effectiveOn, ct);
        if (unitPrice is null)
        {
            // Task 3.5 (GREEN): zero effective rows -> the typed outcome,
            // never a `decimal?` null and never a silent `0m` fallback.
            return new PriceResolutionOutcome.NoEffectivePrice(presentationId, effectiveOn);
        }

        // Task 3.7 (GREEN): Round2 (AwayFromZero) applied EXACTLY TWICE —
        // once for the unit-net price, once for the line total computed
        // from the ALREADY-ROUNDED unit-net price. Never a third rounding
        // pass at a higher (order) level.
        var discount = discountPercentage ?? 0m;
        var unitNet = Money.Round2(unitPrice.Value * (1 - discount / 100m));
        var lineTotal = Money.Round2(unitNet * quantity);

        return new PriceResolutionOutcome.Resolved(unitPrice.Value, discount, unitNet, lineTotal);
    }
}
