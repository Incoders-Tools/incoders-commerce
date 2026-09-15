using Commerce.BranchNode;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;
using Commerce.Domain.Sync;

namespace Commerce.Cloud.Api.Ordering;

public enum OrderSubmissionOutcomeStatus
{
    Accepted,
    Denied
}

public sealed record OrderSubmissionOutcome(OrderSubmissionOutcomeStatus Status, string Reason, Order? Order, bool WasNewlyAccepted);

/// <summary>
/// Cloud-side order acceptance and destination delivery (design.md data flow:
/// "Customer web -> Cloud order/outbox -> Branch inbox/effect -> ACK"). Reuses
/// the exact Unit 3 sync primitives — <see cref="SyncEnvelope"/> and
/// <see cref="BranchSyncStore.ApplyInbound"/> — instead of a parallel delivery
/// pipeline. The order's business identity (<see cref="Order.OrderId"/>) is
/// used as the envelope's <c>OperationId</c>, so retries and offline replays
/// are idempotent through the exact same mechanism already proven by
/// <c>SyncTests.DuplicateInboundDelivery_IsIgnoredAfterFirstApply</c>.
/// Settlement/final stock effect is explicitly out of scope (ADR-003).
/// </summary>
public sealed class CloudOrderStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Order> _orders = new();
    private readonly Func<DateTimeOffset> _clock;

    public CloudOrderStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public OrderSubmissionOutcome Submit(
        CloudTenantScope scope,
        Guid orderId,
        Guid customerId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock)
    {
        lock (_gate)
        {
            if (_orders.TryGetValue(orderId, out var existing))
            {
                // Idempotent: the same business order id never creates a
                // second acceptance or a second delivery attempt.
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, "existing-order", existing, WasNewlyAccepted: false);
            }

            var order = new Order(orderId, scope.OrganizationId, customerId, destinationBranchId, lines, _clock());
            _orders[orderId] = order;

            AttemptDelivery(order, actorId, correlationId, destination, hasAvailableStock);

            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, "accepted", order, WasNewlyAccepted: true);
        }
    }

    /// <summary>
    /// Retries destination delivery for an already-accepted pending order
    /// (offline retry / reconnection) without creating a second order. The
    /// branch's own inbox idempotency key (<c>OperationId</c> =
    /// <see cref="Order.OrderId"/>) guarantees at-most-one applied effect even
    /// if this is called more than once.
    /// </summary>
    public OrderSubmissionOutcome RetryDelivery(Guid orderId, Guid actorId, Guid correlationId, BranchSyncStore destination, bool hasAvailableStock)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, "not-found", null, WasNewlyAccepted: false);
            }

            AttemptDelivery(order, actorId, correlationId, destination, hasAvailableStock);
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, "retried", order, WasNewlyAccepted: false);
        }
    }

    public Order? Find(CloudTenantScope scope, Guid orderId) =>
        _orders.TryGetValue(orderId, out var order) && order.OrganizationId == scope.OrganizationId ? order : null;

    private void AttemptDelivery(Order order, Guid actorId, Guid correlationId, BranchSyncStore? destination, bool hasAvailableStock)
    {
        if (destination is null)
        {
            // Destination branch offline at this attempt (ADR-003): the order
            // stays honestly pending — no stock promise, no silent accept.
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
            OccurredAtUtc: _clock(),
            PayloadKind: "order",
            Payload: "{}");

        var result = destination.ApplyInbound(envelope);
        if (result.Outcome is InboundApplyOutcome.Applied or InboundApplyOutcome.DuplicateIgnored)
        {
            order.MarkDestinationConfirmed();
        }
    }
}
