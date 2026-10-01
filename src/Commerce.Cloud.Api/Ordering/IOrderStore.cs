using Commerce.BranchNode;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Ordering;

public enum OrderSubmissionOutcomeStatus
{
    Accepted,
    Denied
}

public sealed record OrderSubmissionOutcome(OrderSubmissionOutcomeStatus Status, string Reason, Order? Order, bool WasNewlyAccepted);

/// <summary>Reason codes of an <see cref="OrderSubmissionOutcome"/> that the store itself produces.</summary>
public static class OrderSubmissionReasons
{
    public const string Accepted = "accepted";
    public const string ExistingOrder = "existing-order";
    public const string Retried = "retried";
    public const string NotFound = "not-found";
    public const string DestinationBranchNotFound = "destination-branch-not-found";
}

/// <summary>
/// Cloud-side order acceptance (design.md data flow: "Customer web -> Cloud order/outbox ->
/// Branch inbox/effect -> ACK"). Implementations are idempotent on (organization, order id): the
/// same business order id never creates a second order, a second number or a second delivery attempt.
/// </summary>
public interface IOrderStore
{
    /// <summary>
    /// Accepts an order: stores it with its number (<c>P{branch}-W-{sequence}</c>), then attempts
    /// delivery to <paramref name="destination"/>. A destination branch that does not exist in the
    /// organization is a typed denial (<see cref="OrderSubmissionReasons.DestinationBranchNotFound"/>),
    /// never an exception.
    /// </summary>
    Task<OrderSubmissionOutcome> SubmitAsync(
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
        bool hasAvailableStock,
        CancellationToken ct);

    /// <summary>
    /// Retries destination delivery for an already-accepted order (offline retry / reconnection)
    /// without creating a second order, and persists the resulting delivery state.
    /// </summary>
    Task<OrderSubmissionOutcome> RetryDeliveryAsync(
        CloudTenantScope scope, Guid orderId, Guid actorId, Guid correlationId,
        BranchSyncStore destination, bool hasAvailableStock, CancellationToken ct);

    Task<Order?> FindAsync(CloudTenantScope scope, Guid orderId, CancellationToken ct);

    /// <summary>
    /// The organization's orders as a SORT, never a gate (design.md "Non-priority = ranking"):
    /// <see cref="Order.DispatchRank"/> ascending (registered customers first), then
    /// <see cref="Order.SubmittedAtUtc"/> ascending.
    /// </summary>
    Task<IReadOnlyList<Order>> ListPendingAsync(CloudTenantScope scope, CancellationToken ct);
}

public static class OrderStoreExtensions
{
    /// <summary>Registered/staff-submitted path: <see cref="OrderOrigin.RegisteredCustomer"/>, no guest contact, no verification.</summary>
    public static Task<OrderSubmissionOutcome> SubmitRegisteredAsync(
        this IOrderStore store,
        CloudTenantScope scope,
        Guid orderId,
        Guid customerId,
        Guid destinationBranchId,
        Guid actorId,
        IReadOnlyList<OrderLineSnapshot> lines,
        Guid correlationId,
        BranchSyncStore? destination,
        bool hasAvailableStock,
        CancellationToken ct) =>
        store.SubmitAsync(
            scope, orderId, OrderOrigin.RegisteredCustomer, customerId, guestContact: null,
            destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock, ct);
}
