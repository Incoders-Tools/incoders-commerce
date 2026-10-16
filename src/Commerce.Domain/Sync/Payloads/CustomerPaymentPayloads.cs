using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;

namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// Payload kinds of a payment a customer makes at the POS against its current account (a "cobro"). The envelope's
/// aggregate is the payment id, so the cloud can tell that a payment was voided even when the void arrives first.
/// Additive-only, like every payload.
/// </summary>
public static class CustomerPaymentPayloadKinds
{
    public const string Received = "customer-payment.received";
    public const string Voided = "customer-payment.voided";
}

/// <summary>
/// A customer paid part or all of what it owes, at the counter: the amount, how (cash with what was received and the
/// change, card or QR), when, in which cash session and an optional note. The cloud credits it on the customer's
/// current account and puts the money In the branch treasury.
/// </summary>
public sealed record CustomerPaymentReceivedPayloadV1(
    Guid PaymentId,
    Guid CustomerId,
    decimal Amount,
    SaleTender Tender,
    DateTimeOffset ReceivedAtUtc,
    Guid? CashSessionId,
    string? Note);

/// <summary>A payment taken back (voided) with the branch PIN and a reason: the cloud reverses its credit and its money.</summary>
public sealed record CustomerPaymentVoidedPayloadV1(
    Guid PaymentId,
    Guid CustomerId,
    decimal Amount,
    SaleTender Tender,
    DateTimeOffset VoidedAtUtc,
    Guid VoidedByOperatorId,
    DiscountAuthorization Authorization,
    string Reason,
    Guid? CashSessionId);
