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
    public const string VerificationInvalid = "verification-invalid";
}

/// <summary>
/// The confirmed guest verification an order submission spends. The store consumes it in the SAME
/// database transaction that inserts the order, so a failed insert leaves the verification
/// usable and one confirmation can never admit two orders.
/// </summary>
/// <param name="VerificationId">The confirmed ticket.</param>
/// <param name="DocumentId">The document id the ticket was issued for; must match.</param>
/// <param name="ContactAddress">The contact address the ticket was issued for; matched ignoring case.</param>
/// <param name="ConfirmedAfter">The ticket must have been confirmed after this instant (the confirm-to-submit TTL already applied to "now").</param>
/// <summary>
/// Who took a staff-entered order and the note they typed (staff-order-taking). The taker is always the signed-in
/// caller, never a request field; the store records both on the order and audits the submission in the SAME
/// transaction that stores it.
/// </summary>
public sealed record StaffOrderEntry(Guid TakenByUserId, string? Note);

public sealed record GuestVerificationConsumption(
    Guid VerificationId, string DocumentId, string ContactAddress, DateTimeOffset ConfirmedAfter);

/// <summary>
/// Cloud-side order acceptance (design.md data flow: "Customer web -> Cloud order/outbox ->
/// Branch inbox/effect -> ACK"). Implementations are idempotent on (organization, order id): the
/// same business order id never creates a second order, a second number or a second delivery attempt.
/// </summary>
public interface IOrderStore
{
    /// <summary>Rows <see cref="ListPendingAsync"/> returns when the caller does not say.</summary>
    public const int DefaultPendingLimit = 200;

    /// <summary>Hard ceiling of <see cref="ListPendingAsync"/>; a larger limit is clamped to it.</summary>
    public const int MaxPendingLimit = 500;

    /// <summary>
    /// Accepts an order: stores it with its number (<c>P{branch}-W-{sequence}</c>), spends
    /// <paramref name="verification"/> (guest orders) in the same transaction, then attempts delivery
    /// to <paramref name="destination"/>. A destination branch that does not exist in the organization
    /// is a typed denial (<see cref="OrderSubmissionReasons.DestinationBranchNotFound"/>), never an
    /// exception; an unusable verification is <see cref="OrderSubmissionReasons.VerificationInvalid"/>
    /// and leaves nothing stored. Resubmitting an order id that already exists returns that order and
    /// does not spend a verification again (a guest retry must present the ticket that admitted it).
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
        GuestVerificationConsumption? verification,
        CancellationToken ct);

    /// <summary>
    /// Stores an order a staff member took for a registered customer: same numbering, idempotency and delivery as
    /// <see cref="SubmitAsync"/>, plus <paramref name="entry"/> recorded on the order and an
    /// <c>order.staff-submitted</c> audit row (actor = the taker) written only when the order is newly stored.
    /// </summary>
    Task<OrderSubmissionOutcome> SubmitStaffAsync(
        CloudTenantScope scope,
        Guid orderId,
        Guid customerId,
        Guid destinationBranchId,
        StaffOrderEntry entry,
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
    /// The organization's orders still pending for the destination, as a SORT, never a gate (design.md
    /// "Non-priority = ranking"): <see cref="Order.DispatchRank"/> ascending (registered customers first),
    /// then <see cref="Order.SubmittedAtUtc"/> ascending, then the order number. Orders the destination
    /// already confirmed are not listed. At most <paramref name="limit"/> orders (clamped to 1 and
    /// <see cref="MaxPendingLimit"/>) so the read stays bounded as the table grows.
    /// The list is TRUNCATED: when more orders are pending than the limit, the tail (the newest and the
    /// lowest-ranked) is not returned and nothing says so. It is a bounded work queue, not a complete
    /// report; the rest appears as the head is delivered and confirmed. There is no paging yet.
    /// </summary>
    Task<IReadOnlyList<Order>> ListPendingAsync(
        CloudTenantScope scope, int limit = DefaultPendingLimit, CancellationToken ct = default);
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
            destinationBranchId, actorId, lines, correlationId, destination, hasAvailableStock, verification: null, ct);
}
