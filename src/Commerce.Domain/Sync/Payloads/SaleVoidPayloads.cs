using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;

namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// Payload kinds of a POS sale. <see cref="Sale"/> is the original commit; <see cref="Voided"/> annuls a committed sale
/// without touching it: the sale stays as it was and the void is a separate, later record (PRD "voids generate auditable
/// reversals", "no silent deletion"). Additive-only like every payload: a field may be added, never removed or
/// repurposed.
/// </summary>
public static class SalePayloadKinds
{
    public const string Sale = "sale";
    public const string Voided = "sale.voided";
}

/// <summary>
/// The payload of a <c>sale.voided</c> envelope: which sale, when and by whom, the authorization (the branch PIN, the
/// same proof a discount needs), why, and the sale's total and tender so the cloud can audit the reversal without
/// reading the sale back.
/// </summary>
public sealed record SaleVoidedPayloadV1(
    Guid SaleId,
    DateTimeOffset VoidedAtUtc,
    Guid VoidedByOperatorId,
    DiscountAuthorization Authorization,
    string Reason,
    decimal TotalAmount,
    SaleTender? Tender,
    Guid? CashSessionId);
