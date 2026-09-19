namespace Commerce.Domain.Ordering;

/// <summary>
/// An order is never silently "confirmed" without the destination branch
/// having actually applied it (ADR-003): it is honestly pending until then.
/// </summary>
public enum OrderDeliveryStatus
{
    PendingDestination,
    DestinationConfirmed
}

/// <summary>
/// Why an order remains pending — offline destination and unconfirmed stock
/// are both first-class, honest reasons, never conflated with denial.
/// </summary>
public enum OrderPendingReason
{
    None,
    DestinationOffline,
    StockUnconfirmed
}
