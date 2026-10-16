namespace Commerce.Domain.Ordering;

/// <summary>
/// The classification of who submitted an <see cref="Order"/>. Required,
/// explicit, and never inferable only from the presence/absence of
/// <see cref="Order.CustomerId"/> (commerce-guest-ordering spec, "Order
/// Origin Classification").
/// </summary>
public enum OrderOrigin
{
    Guest,
    RegisteredCustomer
}
