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
    public static OrderLineSnapshot Snapshot(Product product, Presentation presentation, decimal quantity) =>
        new(
            product.Id,
            product.Name,
            presentation.Id,
            presentation.Name,
            presentation.QuantityBehavior,
            presentation.UnitId,
            quantity);
}
