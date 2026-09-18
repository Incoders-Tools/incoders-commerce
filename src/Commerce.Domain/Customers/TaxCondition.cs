namespace Commerce.Domain.Customers;

/// <summary>
/// AFIP-style VAT condition — needed for correct invoicing later, cheap to
/// capture now. Not validated against any external authority (AFIP web
/// services are explicitly out of scope).
/// </summary>
public enum TaxCondition
{
    ConsumidorFinal,
    ResponsableInscripto,
    Monotributo,
    Exento,
    NoAplica
}
