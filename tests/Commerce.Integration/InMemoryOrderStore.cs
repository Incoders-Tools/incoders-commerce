using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Integration;

/// <summary>
/// In-memory <see cref="IOrderStore"/> for unit-level tests that do not need a database (delivery,
/// idempotency and ranking semantics). Production uses <c>PostgresOrderStore</c>; persistence, numbering
/// and guest verification are proven against Postgres, so this fake assigns no order number and
/// ignores the verification. Keeps the synchronous API the pre-persistence tests were written against.
/// </summary>
public sealed class InMemoryOrderStore : IOrderStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Order> _orders = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryOrderStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

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
    /// <c>CloudOrderSubmissionService.SubmitGuestAsync</c> for
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
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.ExistingOrder, existing, WasNewlyAccepted: false);
            }

            var order = new Order(orderId, scope.OrganizationId, origin, customerId, guestContact, destinationBranchId, lines, _clock());
            _orders[orderId] = order;

            AttemptDelivery(order, actorId, correlationId, destination, hasAvailableStock);

            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.Accepted, order, WasNewlyAccepted: true);
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
                return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Denied, OrderSubmissionReasons.NotFound, null, WasNewlyAccepted: false);
            }

            AttemptDelivery(order, actorId, correlationId, destination, hasAvailableStock);
            return new OrderSubmissionOutcome(OrderSubmissionOutcomeStatus.Accepted, OrderSubmissionReasons.Retried, order, WasNewlyAccepted: false);
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

    private void AttemptDelivery(Order order, Guid actorId, Guid correlationId, BranchSyncStore? destination, bool hasAvailableStock) =>
        OrderDelivery.Attempt(order, actorId, correlationId, destination, hasAvailableStock, _clock());

    // --- IOrderStore -----------------------------------------------------------------------------

    Task<OrderSubmissionOutcome> IOrderStore.SubmitAsync(
        CloudTenantScope scope, Guid orderId, OrderOrigin origin, Guid? customerId, GuestContact? guestContact,
        Guid destinationBranchId, Guid actorId, IReadOnlyList<OrderLineSnapshot> lines, Guid correlationId,
        BranchSyncStore? destination, bool hasAvailableStock, CancellationToken ct) =>
        Task.FromResult(Submit(
            scope, orderId, origin, customerId, guestContact, destinationBranchId, actorId, lines, correlationId,
            destination, hasAvailableStock));

    Task<OrderSubmissionOutcome> IOrderStore.RetryDeliveryAsync(
        CloudTenantScope scope, Guid orderId, Guid actorId, Guid correlationId,
        BranchSyncStore destination, bool hasAvailableStock, CancellationToken ct) =>
        Task.FromResult(RetryDelivery(orderId, actorId, correlationId, destination, hasAvailableStock));

    Task<Order?> IOrderStore.FindAsync(CloudTenantScope scope, Guid orderId, CancellationToken ct) =>
        Task.FromResult(Find(scope, orderId));

    Task<IReadOnlyList<Order>> IOrderStore.ListPendingAsync(CloudTenantScope scope, CancellationToken ct) =>
        Task.FromResult(ListPending(scope));
}
