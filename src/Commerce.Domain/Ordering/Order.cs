namespace Commerce.Domain.Ordering;

/// <summary>
/// A submitted private order. <see cref="OrderId"/> is the stable business
/// identity reused as the sync <c>OperationId</c> (ADR-003 idempotency).
/// Organization/customer/destination/lines are immutable after construction —
/// nothing here changes if the customer's ordering access is later revoked or
/// the catalogue changes; only <see cref="Status"/>/<see cref="PendingReason"/>
/// evolve as destination delivery progresses.
/// </summary>
public sealed class Order
{
    public Guid OrderId { get; }
    public Guid OrganizationId { get; }

    /// <summary>
    /// The origin of this order. Required, explicit, and paired with
    /// <see cref="CustomerId"/>/<see cref="GuestContact"/> by the constructor
    /// invariant below (commerce-guest-ordering spec, "Order Construction
    /// Invariant").
    /// </summary>
    public OrderOrigin Origin { get; }

    /// <summary>
    /// The resolved customer for a <see cref="OrderOrigin.RegisteredCustomer"/>
    /// order; always null for a <see cref="OrderOrigin.Guest"/> order.
    /// </summary>
    public Guid? CustomerId { get; }

    /// <summary>
    /// The guest's captured identity for a <see cref="OrderOrigin.Guest"/>
    /// order; always null for a <see cref="OrderOrigin.RegisteredCustomer"/>
    /// order (commerce-guest-ordering spec, "Guest Identity Capture on the
    /// Order").
    /// </summary>
    public GuestContact? GuestContact { get; }

    public Guid DestinationBranchId { get; }
    public IReadOnlyList<OrderLineSnapshot> Lines { get; }
    public DateTimeOffset SubmittedAtUtc { get; }
    public OrderDeliveryStatus Status { get; private set; }
    public OrderPendingReason PendingReason { get; private set; }

    /// <summary>
    /// A derived, read-only sort key: registered orders rank above guest
    /// orders when a branch prioritizes pending work. Never consulted by
    /// delivery logic (<see cref="MarkDestinationConfirmed"/>) — ADR-009's
    /// "non-priority" is a ranking attribute, never a gate.
    /// </summary>
    public int DispatchRank => Origin == OrderOrigin.RegisteredCustomer ? 0 : 1;

    public Order(
        Guid orderId,
        Guid organizationId,
        OrderOrigin origin,
        Guid? customerId,
        GuestContact? guestContact,
        Guid destinationBranchId,
        IReadOnlyList<OrderLineSnapshot> lines,
        DateTimeOffset submittedAtUtc)
    {
        if (origin == OrderOrigin.RegisteredCustomer &&
            (customerId is null || customerId == Guid.Empty || guestContact is not null))
        {
            // commerce-guest-ordering spec "Order Construction Invariant":
            // RegisteredCustomer => CustomerId present and no GuestContact.
            // Replaces the old unconditional non-empty CustomerId guard.
            throw new ArgumentException(
                "A registered order requires a non-empty CustomerId and carries no GuestContact.", nameof(origin));
        }

        if (origin == OrderOrigin.Guest && (customerId is not null || guestContact is null))
        {
            // Guest => CustomerId null and GuestContact present.
            throw new ArgumentException(
                "A guest order carries a GuestContact and no CustomerId.", nameof(origin));
        }

        OrderId = orderId;
        OrganizationId = organizationId;
        Origin = origin;
        CustomerId = customerId;
        GuestContact = guestContact;
        DestinationBranchId = destinationBranchId;
        Lines = lines;
        SubmittedAtUtc = submittedAtUtc;
        Status = OrderDeliveryStatus.PendingDestination;
        PendingReason = OrderPendingReason.None;
    }

    public void MarkPending(OrderPendingReason reason)
    {
        Status = OrderDeliveryStatus.PendingDestination;
        PendingReason = reason;
    }

    public void MarkDestinationConfirmed()
    {
        Status = OrderDeliveryStatus.DestinationConfirmed;
        PendingReason = OrderPendingReason.None;
    }
}
