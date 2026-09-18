using Commerce.Application.Pricing;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;

namespace Commerce.Application.Ordering;

/// <summary>
/// Freezes Product/Presentation commercial meaning into an
/// <see cref="OrderLineSnapshot"/> at submission time (ADR-003), reusing the
/// Unit 2 Catalog types directly instead of a parallel order-line model.
/// </summary>
public static class OrderSnapshotFactory
{
    /// <summary>
    /// Pre-pricing-engine overload, kept for the existing snapshot-semantics
    /// tests that predate ADR-010 and do not assert price fields. Fills the
    /// four price fields with <c>0m</c> — never used by the real order
    /// submission path, which always goes through the resolved-price
    /// overload below.
    /// </summary>
    public static OrderLineSnapshot Snapshot(Product product, Presentation presentation, decimal quantity) =>
        new(
            product.Id,
            product.Name,
            presentation.Id,
            presentation.Name,
            presentation.QuantityBehavior,
            presentation.UnitId,
            quantity,
            UnitListPrice: 0m,
            AppliedDiscountPercentage: 0m,
            UnitNetPrice: 0m,
            LineTotal: 0m);

    /// <summary>
    /// commerce-pricing-engine design.md "OrderLineSnapshot extension and
    /// where resolution runs": the ONLY overload the real order submission
    /// path uses. <paramref name="resolvedPrice"/> comes from
    /// <see cref="PricingResolutionService.ResolveAsync"/>, called strictly
    /// AFTER every access/binding/customer-enabled denial check.
    /// </summary>
    public static OrderLineSnapshot Snapshot(
        Product product, Presentation presentation, decimal quantity, PriceResolutionOutcome.Resolved resolvedPrice) =>
        new(
            product.Id,
            product.Name,
            presentation.Id,
            presentation.Name,
            presentation.QuantityBehavior,
            presentation.UnitId,
            quantity,
            resolvedPrice.UnitListPrice,
            resolvedPrice.AppliedDiscountPercentage,
            resolvedPrice.UnitNetPrice,
            resolvedPrice.LineTotal);
}
