using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;

namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// The real payload carried by a <c>"sale"</c>-kind <see cref="SyncEnvelope"/>
/// (commerce-sync-ownership design.md File Changes). Replaces the decorative
/// literal <c>"{}"</c> that <c>BranchNodeService.CompleteOfflineSale</c>/
/// <c>CompleteScannedSale</c> previously produced. Additive-evolution rule
/// (Requirement: Payload-Kind Versioning): a field may be added later, never
/// removed, renamed, or repurposed — a breaking change ships as a new
/// <c>payload_kind</c> (for example <c>"sale.v2"</c>) instead. Discounts follow that rule: the sale
/// discount and its authorization marker are optional trailing fields (and each
/// line carries its own optional discount), all null when nothing was
/// discounted, so a payload written before discounts existed still reads. The
/// tender (how the customer paid) follows the same rule: an optional trailing
/// field, null on a payload written before tenders existed. So is the cash
/// session the sale belongs to (<see cref="CashSessionId"/>), null on a payload
/// written before sessions existed.
/// The human sale number follows it too: <see cref="BranchCode"/>, <see cref="RegisterNumber"/>
/// and <see cref="SaleSequence"/> (`V01-C2-125`) are optional trailing fields, all null when the
/// terminal did not know its register when it committed the sale. The server treats them as
/// a claim to verify, never as authority (ingestion is never blocked by them).
/// <see cref="TotalAmount"/> is the FINAL total, after every discount.
/// </summary>
public sealed record SalePayloadV1(
    Guid SaleId,
    decimal TotalAmount,
    string SaleKind,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<SaleLine> Lines,
    Guid? CustomerId = null,
    decimal? SaleDiscountPercent = null,
    decimal? SaleDiscountAmount = null,
    DiscountAuthorization? DiscountAuthorization = null,
    SaleTender? Tender = null,
    Guid? CashSessionId = null,
    int? BranchCode = null,
    int? RegisterNumber = null,
    int? SaleSequence = null);
