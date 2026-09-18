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
