using Commerce.Domain.Catalog;

namespace Commerce.Domain.Ordering;

/// <summary>
/// Frozen Product/Presentation commercial meaning at submission time (ADR-003
/// "Order snapshots"), reusing the Unit 2 Catalog types directly instead of a
/// parallel order-line model, so later catalogue edits never retroactively
/// change an already-submitted order.
/// </summary>
public sealed record OrderLineSnapshot(
    Guid ProductId,
    string ProductName,
    Guid PresentationId,
    string PresentationName,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    decimal Quantity);
