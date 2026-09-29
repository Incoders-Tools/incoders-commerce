using Commerce.Domain.Discounts;

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
/// discounted, so a payload written before discounts existed still reads.
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
    DiscountAuthorization? DiscountAuthorization = null);
