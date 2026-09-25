namespace Commerce.Domain.Pricing;

/// <summary>
/// An ordered set of <see cref="RateComponent"/> rows published for one date
/// (commerce-price-composition design.md "Effective dating" and "Versioning
/// granularity"). The SET is the effective-dated unit: a dated set fully
/// replaces the previously effective set for its owner, so changing one rate
/// publishes a new complete set rather than assembling a composition from
/// independently dated rows where an accidental omission would silently drop
/// a tax instead of failing visibly.
///
/// Append-only with deliberately NO `EffectiveTo`, exactly mirroring
/// <see cref="PriceListEntry"/>: a rate change is an INSERT of a set with a
/// later <see cref="EffectiveFrom"/>, never an UPDATE of a prior one, and
/// "no components effective for this date" is exactly ZERO matching sets, not
/// a gap between two ranges. Enforced at the database by
/// `GRANT SELECT, INSERT` only (migration 0013), not merely by this class.
///
/// The owner is EITHER a price list (<see cref="ForPriceList"/>) or the
/// organization itself (<see cref="ForOrganizationDefault"/>, the inheritable
/// default). Inheritance is all-or-nothing at the set level and is resolved
/// by the persistence layer, never merged per component.
/// </summary>
public sealed class RateComponentSet
{
    public Guid Id { get; }

    public Guid OrganizationId { get; }

    /// <summary>
    /// The owning price list, or `null` for the organization's inheritable
    /// default set. Components live on the LIST, not the organization
    /// (design.md decision 1): the real sheet is titled "Reparto" — its 7%
    /// freight exists because it is the DELIVERY list, and a counter list in
    /// the same organization would not carry it.
    /// </summary>
    public Guid? PriceListId { get; }

    public bool IsOrganizationDefault => PriceListId is null;

    public DateOnly EffectiveFrom { get; }

    /// <summary>Sorted by <see cref="RateComponent.Order"/>; may legitimately be empty.</summary>
    public IReadOnlyList<RateComponent> Components { get; }

    private RateComponentSet(
        Guid id,
        Guid organizationId,
        Guid? priceListId,
        DateOnly effectiveFrom,
        IReadOnlyList<RateComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var duplicateCode = components
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateCode is not null)
        {
            throw new ArgumentException(
                $"components must not repeat a code within one set; '{duplicateCode.Key}' appears {duplicateCode.Count()} times.",
                nameof(components));
        }

        var duplicateOrder = components
            .GroupBy(c => c.Order)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateOrder is not null)
        {
            // Two components sharing an order make the composition depend on
            // row identity, which is unreviewable and untestable the moment
            // one of them switches to `Subtotal`.
            throw new ArgumentException(
                $"components must not repeat an order within one set; order {duplicateOrder.Key} appears {duplicateOrder.Count()} times.",
                nameof(components));
        }

        Id = id;
        OrganizationId = organizationId;
        PriceListId = priceListId;
        EffectiveFrom = effectiveFrom;
        Components = [.. components.OrderBy(c => c.Order)];
    }

    /// <summary>A set owned by one price list; it fully overrides the organization's defaults.</summary>
    public static RateComponentSet ForPriceList(
        Guid id,
        Guid organizationId,
        Guid priceListId,
        DateOnly effectiveFrom,
        IReadOnlyList<RateComponent> components) =>
        new(id, organizationId, priceListId, effectiveFrom, components);

    /// <summary>
    /// The organization's default set, used by any of its price lists that
    /// declares no set of its own (spec "Organization Default Rate
    /// Components").
    /// </summary>
    public static RateComponentSet ForOrganizationDefault(
        Guid id,
        Guid organizationId,
        DateOnly effectiveFrom,
        IReadOnlyList<RateComponent> components) =>
        new(id, organizationId, null, effectiveFrom, components);

    /// <summary>
    /// Derives a final list price from a base price by applying every
    /// component in <see cref="RateComponent.Order"/>, each against its own
    /// declared <see cref="RateCalculationBase"/>.
    ///
    /// An EMPTY set returns <paramref name="basePrice"/> unchanged — the
    /// property that makes redefining `PriceListEntry.UnitPrice` as a base
    /// price a semantic reinterpretation with zero data rewrite.
    ///
    /// Deliberately does NO rounding: this change preserves whatever rounding
    /// resolution already applies and introduces neither a new money type nor
    /// per-component rounding.
    ///
    /// Called by `PricingResolutionService` as of slice 2, between reading the
    /// effective entry and applying the customer's `DiscountPercentage` — that
    /// position is the contract, not an implementation detail (spec
    /// "Resolution Composes Rate Components Before The Customer Discount").
    /// </summary>
    public decimal Compose(decimal basePrice)
    {
        var subtotal = basePrice;

        foreach (var component in Components)
        {
            var operand = component.CalculationBase switch
            {
                RateCalculationBase.Base => basePrice,
                RateCalculationBase.Subtotal => subtotal,
                _ => throw new InvalidOperationException(
                    $"Component '{component.Code}' has no declared calculation base."),
            };

            subtotal += operand * component.Percentage / 100m;
        }

        return subtotal;
    }
}
