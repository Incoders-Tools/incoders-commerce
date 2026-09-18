using Commerce.Domain.Catalog;

namespace Commerce.Domain.Ordering;

/// <summary>
/// Frozen Product/Presentation commercial meaning at submission time (ADR-003
/// "Order snapshots"), reusing the Unit 2 Catalog types directly instead of a
/// parallel order-line model, so later catalogue edits never retroactively
/// change an already-submitted order.
///
/// commerce-pricing-engine design.md "OrderLineSnapshot extension and where
/// resolution runs": the four price fields are populated ONLY server-side, by
/// <see cref="Commerce.Application.Ordering.OrderSnapshotFactory"/> from a
/// <c>PriceResolutionOutcome.Resolved</c> — a client has nowhere to assert a
/// price (see <c>SubmitOrderLine</c>, which carries no price member at all).
/// Once frozen here, a later price publication never alters these values
/// (append-only history is not retroactive).
/// </summary>
public sealed record OrderLineSnapshot(
    Guid ProductId,
    string ProductName,
    Guid PresentationId,
    string PresentationName,
    QuantityBehavior QuantityBehavior,
    Guid UnitId,
    decimal Quantity,
    decimal UnitListPrice,
    decimal AppliedDiscountPercentage,
    decimal UnitNetPrice,
    decimal LineTotal);
