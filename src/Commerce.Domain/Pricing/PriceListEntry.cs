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
    /// SLICE BOUNDARY: as of slice 1 this is a documentation change only.
    /// `PricingResolutionService` still returns this value directly and no
    /// resolved price has changed; wiring
    /// <see cref="RateComponentSet.Compose"/> in between reading the entry
    /// and applying the customer discount is slice 2 (the
    /// `pricing-resolution` spec delta).
    ///
    /// OPERATIONAL NOTE for that cutover (design.md "Rollback Plan"): rows
    /// already imported for Vaca Verde hold `15,370`-style FINAL prices. If a
    /// 1.45 composition is switched on before base-priced entries are
    /// published with the same `EffectiveFrom`, those rows would resolve to
    /// `15,370 x 1.45`.
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
