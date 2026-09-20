namespace Commerce.Domain.Payments;

/// <summary>
/// Payment method taxonomy (commerce-payments design.md "One aggregate for
/// POS cash and future online" — answered product question (a)). Mercado
/// Pago is a first-class member, deliberately not merged into a generic
/// <see cref="Card"/> bucket.
/// </summary>
public enum PaymentMethod
{
    Cash,
    AccountCredit,
    BankTransfer,
    Card,
    MercadoPago
}
