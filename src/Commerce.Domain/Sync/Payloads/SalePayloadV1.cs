namespace Commerce.Domain.Sync.Payloads;

/// <summary>
/// The real payload carried by a <c>"sale"</c>-kind <see cref="SyncEnvelope"/>
/// (commerce-sync-ownership design.md File Changes). Replaces the decorative
/// literal <c>"{}"</c> that <c>BranchNodeService.CompleteOfflineSale</c>/
/// <c>CompleteScannedSale</c> previously produced. Additive-evolution rule
/// (Requirement: Payload-Kind Versioning): a field may be added later, never
/// removed, renamed, or repurposed — a breaking change ships as a new
/// <c>payload_kind</c> (for example <c>"sale.v2"</c>) instead.
/// </summary>
public sealed record SalePayloadV1(
    Guid SaleId,
    decimal TotalAmount,
    string SaleKind,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyList<SaleLine> Lines);
