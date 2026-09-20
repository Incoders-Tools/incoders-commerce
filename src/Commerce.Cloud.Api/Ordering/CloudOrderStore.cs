using Commerce.BranchNode;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

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

    /// <summary>
    /// Registered/staff-submitted path. Delegates to the general overload
    /// below with <see cref="OrderOrigin.RegisteredCustomer"/> and no
    /// <see cref="GuestContact"/> — kept as a distinct overload (rather than
    /// requiring every existing caller to pass origin/guestContact
    /// explicitly) so this call site is byte-identical before and after
    /// commerce-guest-ordering Unit 4.
    /// </summary>
    public OrderSubmissionOutcome Submit(
        CloudTenantScope scope,
        Guid orderId,
        Guid customerId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock) =>
        Submit(
            scope, orderId, OrderOrigin.RegisteredCustomer, customerId, guestContact: null,
            destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock);

    /// <summary>
    /// General submission path (commerce-guest-ordering design.md "File
    /// Changes": <c>Submit</c> takes <c>OrderOrigin</c>, nullable
    /// <c>customerId</c>, and <c>GuestContact?</c>). Used directly by
    /// <see cref="CloudOrderSubmissionService.SubmitGuestAsync"/> for
    /// <see cref="OrderOrigin.Guest"/> orders.
    /// </summary>
    public OrderSubmissionOutcome Submit(
        CloudTenantScope scope,
        Guid orderId,
        OrderOrigin origin,
        Guid? customerId,
        GuestContact? guestContact,
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

            var order = new Order(orderId, scope.OrganizationId, origin, customerId, guestContact, destinationBranchId, lines, _clock());
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

    /// <summary>
    /// Phase 8 follow-up C (commerce-guest-ordering verify-report.md
    /// WARNING 3): the first consumer of <see cref="Order.DispatchRank"/> as
    /// a SORT KEY, never a gate (design.md "Non-priority = ranking, never a
    /// gate" — <see cref="AttemptDelivery"/> above is completely untouched
    /// by this method). Orders for the organization, ordered by
    /// <see cref="Order.DispatchRank"/> ascending (registered customers
    /// first) then <see cref="Order.SubmittedAtUtc"/> ascending
    /// (submission order within the same rank) — design.md File Changes:
    /// "pending-list reads ordered by DispatchRank then SubmittedAtUtc".
    /// </summary>
    public IReadOnlyList<Order> ListPending(CloudTenantScope scope)
    {
        lock (_gate)
        {
            return _orders.Values
                .Where(order => order.OrganizationId == scope.OrganizationId)
                .OrderBy(order => order.DispatchRank)
                .ThenBy(order => order.SubmittedAtUtc)
                .ToList();
        }
    }

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

        var payload = new OrderPayloadV1(
            OrderId: order.OrderId,
            DestinationBranchId: order.DestinationBranchId,
            Origin: order.Origin.ToString(),
            Lines: order.Lines
                .Select(line => new OrderLinePayloadV1(
                    line.ProductId, line.ProductName, line.PresentationId, line.PresentationName,
                    line.Quantity, line.UnitNetPrice, line.LineTotal))
                .ToList());

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
            Payload: SyncPayloadCodec.Serialize(payload));

        var result = destination.ApplyInbound(envelope);
        // Task 3.8/3.9: an UnknownKind outcome is treated as not-confirmed —
        // the order stays Pending, never Applied/DuplicateIgnored (design.md
        // File Changes: "treat UnknownKind as not-confirmed").
        if (result.Outcome is InboundApplyOutcome.Applied or InboundApplyOutcome.DuplicateIgnored)
        {
            order.MarkDestinationConfirmed();
        }
    }
}
