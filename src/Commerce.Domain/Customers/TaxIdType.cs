namespace Commerce.Domain.Customers;

/// <summary>
/// Argentine fiscal identifier kind, kept as one typed pair with
/// <c>Customer.TaxId</c> rather than two always-nullable columns.
/// </summary>
public enum TaxIdType
{
    None,
    Cuit,
    Cuil
}
