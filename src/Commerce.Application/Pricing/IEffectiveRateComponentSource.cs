using Commerce.Domain.Pricing;

namespace Commerce.Application.Pricing;

/// <summary>
/// The composition half of the resolution ports (commerce-price-composition
/// design.md "Resolution placement"): supplies the
/// <see cref="RateComponentSet"/> effective on a date for the price list the
/// implementation is already bound to, exactly as
/// <see cref="IEffectivePriceSource"/> supplies the effective base price.
///
/// Deliberately mirrors <see cref="IEffectivePriceSource"/> in three ways,
/// because the "Composition Is Part Of The Single Resolution Authority"
/// requirement is enforced by these shapes rather than by review:
///
/// 1. NO channel or caller-identity parameter — a client cannot ask for "its"
///    composition, so web, POS and admin console cannot diverge.
/// 2. NO caller-supplied component set — there is no parameter through which a
///    submission could push its own rates, so a caller-composed price is
///    unexpressible, not merely forbidden.
/// 3. The price list is bound by the IMPLEMENTATION (once per scoped
///    instance), not passed per call, so the set and the entry are read for the
///    same list by construction.
///
/// The date parameter is the RESOLUTION date, selected independently of which
/// <see cref="PriceListEntry"/> is effective (spec "Composition Uses The Rates
/// Effective On The Resolution Date").
///
/// Returning `null` means exactly zero effective sets for that date — for the
/// list AND for its organization's defaults. That is a legitimate EMPTY
/// composition which <see cref="PricingResolutionService"/> resolves to the
/// base price itself; it is never an error and never a substituted default
/// rate. This is the opposite of a `null` from
/// <see cref="IEffectivePriceSource"/>, which IS an explicit error.
/// </summary>
public interface IEffectiveRateComponentSource
{
    Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct);
}
