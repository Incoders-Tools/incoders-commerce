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
    private readonly IEffectiveRateComponentSource? _rateComponentSource;

    /// <summary>
    /// <paramref name="rateComponentSource"/> is optional, and omitting it is
    /// NOT the same kind of absence as a missing price: a host with no
    /// component source composes every price as the identity, which is exactly
    /// what an empty effective set does (spec "Empty Composition Resolves To
    /// The Base Price"). The POS uses that today — its replicated
    /// `price_replica` carries prices, not rate components — so its resolved
    /// prices are unchanged by this slice. That gap is a REPLICATION gap, not a
    /// resolution one: the arithmetic lives here, in the one compiled method
    /// both hosts run, and the day the replica carries components the POS
    /// passes a source and composes identically with no code change here.
    /// </summary>
    public PricingResolutionService(
        IEffectivePriceSource priceSource,
        IEffectiveRateComponentSource? rateComponentSource = null)
    {
        _priceSource = priceSource;
        _rateComponentSource = rateComponentSource;
    }

    /// <summary>
    /// `discountPercentage == null` => GUEST: the official list price, no
    /// discount. `discountPercentage != null` => REGISTERED: the customer's
    /// own `DiscountPercentage`. Rounding (`Money.Round2`, AwayFromZero) is
    /// applied EXACTLY TWICE: once for the unit-net price, once for the line
    /// total — never a third time at any higher level.
    ///
    /// commerce-price-composition, spec "Resolution Composes Rate Components
    /// Before The Customer Discount": the three steps below are ORDERED, and
    /// the order is part of the contract — base price, then composition, then
    /// the customer discount.
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

        // STEP 2 — compose. `unitPrice` is the BASE price (the `PriceListEntry`
        // amount, respecified by this change); the FINAL LIST PRICE is derived
        // from it here and is never persisted as a second column.
        //
        // The set is selected by the RESOLUTION date, independently of which
        // entry is effective (spec "Composition Uses The Rates Effective On The
        // Resolution Date"), and `null` — no set for the list and none for its
        // organization — composes to the base itself. That identity is the
        // whole reason this slice reprices nothing that was already loaded.
        var basePrice = unitPrice.Value;
        var effectiveComponents = _rateComponentSource is null
            ? null
            : await _rateComponentSource.GetEffectiveSetAsync(effectiveOn, ct);
        var listPrice = effectiveComponents?.Compose(basePrice) ?? basePrice;

        // STEP 3 — and only now the customer's discount, against the COMPOSED
        // list price. Never against `basePrice`: discounting first would mean
        // the markup component re-inflates the discount, and it would silently
        // change what a discount means for every existing customer.
        //
        // Both steps are multiplications, so they commute in the NET price —
        // the order is observable in `UnitListPrice`, which is why the spec
        // defines the list price as the composed one and
        // `PricingCompositionTests` asserts it there.
        //
        // Task 3.7 (GREEN), unchanged: Round2 (AwayFromZero) applied EXACTLY
        // TWICE — once for the unit-net price, once for the line total computed
        // from the ALREADY-ROUNDED unit-net price. Composition itself does NOT
        // round, so this change adds no third rounding pass.
        var discount = discountPercentage ?? 0m;
        var unitNet = Money.Round2(listPrice * (1 - discount / 100m));
        var lineTotal = Money.Round2(unitNet * quantity);

        return new PriceResolutionOutcome.Resolved(listPrice, discount, unitNet, lineTotal);
    }
}
