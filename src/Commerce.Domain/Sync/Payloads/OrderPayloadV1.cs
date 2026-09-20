namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// One frozen order line inside an <see cref="OrderPayloadV1"/> — a
/// commercial-meaning snapshot at submission time, not a live catalogue
/// reference (mirrors <c>Commerce.Domain.Ordering.OrderLineSnapshot</c>'s
/// "frozen" intent without taking a dependency on the Ordering namespace from
/// this payload record).
/// </summary>
public sealed record OrderLinePayloadV1(
    Guid ProductId,
    string ProductName,
    Guid PresentationId,
    string PresentationName,
    decimal Quantity,
    decimal UnitNetPrice,
    decimal LineTotal);

/// <summary>
/// The real payload carried by an <c>"order"</c>-kind <see cref="SyncEnvelope"/>
/// (commerce-sync-ownership design.md File Changes). Replaces the decorative
/// literal <c>"{}"</c> that <c>CloudOrderStore.AttemptDelivery</c> previously
/// produced. Additive-evolution rule (Requirement: Payload-Kind Versioning):
/// a field may be added later, never removed, renamed, or repurposed.
/// </summary>
public sealed record OrderPayloadV1(
    Guid OrderId,
    Guid DestinationBranchId,
    string Origin,
    IReadOnlyList<OrderLinePayloadV1> Lines);
