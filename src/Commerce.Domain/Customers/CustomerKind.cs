namespace Commerce.Domain.Customers;

/// <summary>
/// `Retail` (B2C — final consumer, online/counter orders) or `Wholesale`
/// (B2B — other butcheries/resellers, delivery/distribution). Drives which
/// fields are expected to be filled and, later, which price list/discount
/// tier applies (ADR-010, Phase C).
/// </summary>
public enum CustomerKind
{
    Retail,
    Wholesale
}
