using Commerce.BranchNode;
using Commerce.Domain.Ordering;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Cloud.Api.Ordering;

/// <summary>
/// Destination delivery of an accepted order, shared by every <see cref="IOrderStore"/>. Reuses the
/// exact Unit 3 sync primitives (<see cref="SyncEnvelope"/> and <see cref="BranchSyncStore.ApplyInbound"/>)
/// instead of a parallel delivery pipeline. The order business identity (<see cref="Order.OrderId"/>)
/// is the envelope <c>OperationId</c>, so retries and offline replays are idempotent through the same
/// mechanism <c>SyncTests.DuplicateInboundDelivery_IsIgnoredAfterFirstApply</c> proves. Settlement and the
/// final stock effect are out of scope (ADR-003).
/// </summary>
public static class OrderDelivery
{
    /// <summary>
    /// Attempts delivery and moves <paramref name="order"/> to its honest state: pending (destination
    /// offline / stock unconfirmed) or confirmed by the destination inbox.
    /// </summary>
    public static void Attempt(
        Order order, Guid actorId, Guid correlationId, BranchSyncStore? destination, bool hasAvailableStock, DateTimeOffset now)
    {
        if (destination is null)
        {
            // Destination branch offline at this attempt (ADR-003): the order
            // stays honestly pending - no stock promise, no silent accept.
            order.MarkPending(OrderPendingReason.DestinationOffline);
            return;
        }

        if (!hasAvailableStock)
        {
            order.MarkPending(OrderPendingReason.StockUnconfirmed);
            return;
        }

        var envelope = new SyncEnvelope(
            OperationId: order.OrderId,
            ContractVersion: 1,
            OrganizationId: order.OrganizationId,
            BranchId: order.DestinationBranchId,
            AggregateId: order.OrderId,
            AggregateVersion: 1,
            ActorId: actorId,
            CorrelationId: correlationId,
            OccurredAtUtc: now,
            PayloadKind: "order",
            Payload: SyncPayloadCodec.Serialize(BuildPayload(order)));

        var result = destination.ApplyInbound(envelope);
        // Task 3.8/3.9: an UnknownKind outcome is treated as not-confirmed -
        // the order stays Pending, never Applied/DuplicateIgnored.
        if (result.Outcome is InboundApplyOutcome.Applied or InboundApplyOutcome.DuplicateIgnored)
        {
            order.MarkDestinationConfirmed();
        }
    }

    public static OrderPayloadV1 BuildPayload(Order order) => new(
        OrderId: order.OrderId,
        DestinationBranchId: order.DestinationBranchId,
        Origin: order.Origin.ToString(),
        Lines: order.Lines
            .Select(line => new OrderLinePayloadV1(
                line.ProductId, line.ProductName, line.PresentationId, line.PresentationName,
                line.Quantity, line.UnitNetPrice, line.LineTotal))
            .ToList());
}
