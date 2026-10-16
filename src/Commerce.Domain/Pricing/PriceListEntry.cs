namespace Commerce.Domain.Pricing;

/// <summary>
/// One append-only price publication (commerce-pricing-engine design.md
/// "Effective-dating shape"). Deliberately has NO `EffectiveTo` — a
/// supersede is a pure INSERT of a new entry with a later
/// <see cref="EffectiveFrom"/>, never an UPDATE of a prior one. "No
/// effective price for this date" is exactly ZERO matching entries, not a
/// gap between two ranges. This type is an immutable historical fact, not a
/// mutable current-price cell.
/// </summary>
public sealed class PriceListEntry
{
    public Guid Id { get; }
    public Guid PriceListId { get; }
    public Guid PresentationId { get; }

    /// <summary>
    /// The BASE price, from which the final list price is DERIVED by applying
    /// the price list's effective <see cref="RateComponentSet"/>
    /// (commerce-price-composition, spec "Price List Entry Amount Is A Base
    /// Price"). It is not the fully-loaded price the customer pays.
    ///
    /// This is a reinterpretation of the same stored number, with NO data
    /// rewrite and NO second stored field: the composed final price is
    /// deliberately never persisted alongside it, because two stored numbers
    /// that must agree eventually disagree. An entry whose effective
    /// composition is empty composes to exactly this amount, which is why
    /// every entry written before rate components existed keeps resolving to
    /// the number it always resolved to.
    ///
    /// LIVE as of slice 2: `PricingResolutionService` now reads this amount as
    /// the base, applies the effective <see cref="RateComponentSet"/>, and only
    /// then the customer's discount. Entries on a list that has published no
    /// component set still resolve to exactly this number, because an empty
    /// composition is the identity — that is why turning slice 2 on repriced
    /// nothing, and `PricingCompositionTests` guards it against a real database.
    ///
    /// CUTOVER HAZARD — the one way this model can silently misprice, stated
    /// here because the data cannot distinguish the two cases and no code check
    /// can: every entry written BEFORE this change holds a FINAL price, and
    /// this type has no field recording which meaning an amount carries.
    /// Publishing a component set over such rows composes a final price a
    /// second time — for Vaca Verde, `15,370 x 1.45`, a silent 45% increase.
    /// Nothing in the domain or the database can detect that, because a
    /// legitimate adoption and a mistaken one are byte-identical INSERTs.
    ///
    /// The mitigation is therefore ORDERING, not validation, and it is an
    /// operational requirement: publish the base-priced entries and the
    /// component set with the SAME `EffectiveFrom`, as one dated cutover. See
    /// `openspec/changes/commerce-price-composition/design.md`, "Cutover
    /// Runbook", for the sequence and its verification query.
    /// </summary>
    public decimal UnitPrice { get; }

    public DateOnly EffectiveFrom { get; }
    public string Source { get; }
    public Guid? ImportBatchId { get; }

    public PriceListEntry(
        Guid id,
        Guid priceListId,
        Guid presentationId,
        decimal unitPrice,
        DateOnly effectiveFrom,
        string source = "Manual",
        Guid? importBatchId = null)
    {
        if (unitPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "unitPrice must be greater than zero.");
        }

        Id = id;
        PriceListId = priceListId;
        PresentationId = presentationId;
        UnitPrice = unitPrice;
        EffectiveFrom = effectiveFrom;
        Source = source;
        ImportBatchId = importBatchId;
    }
}
