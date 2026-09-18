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
    public Guid CustomerId { get; }
    public Guid DestinationBranchId { get; }
    public IReadOnlyList<OrderLineSnapshot> Lines { get; }
    public DateTimeOffset SubmittedAtUtc { get; }
    public OrderDeliveryStatus Status { get; private set; }
    public OrderPendingReason PendingReason { get; private set; }

    public Order(
        Guid orderId,
        Guid organizationId,
        Guid customerId,
        Guid destinationBranchId,
        IReadOnlyList<OrderLineSnapshot> lines,
        DateTimeOffset submittedAtUtc)
    {
        if (customerId == Guid.Empty)
        {
            // commerce-customer-identity design.md "Order.CustomerId
            // referential integrity, given no orders table exists": an
            // Order with a dangling/absent CustomerId is never constructed.
            throw new ArgumentException("CustomerId must not be Guid.Empty.", nameof(customerId));
        }

        OrderId = orderId;
        OrganizationId = organizationId;
        CustomerId = customerId;
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
