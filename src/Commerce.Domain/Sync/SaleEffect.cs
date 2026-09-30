using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;

namespace Commerce.Domain.Sync;

/// <summary>
/// The durable business effect of a local sale. Branch-owned per ADR-002:
/// committed once, atomically, alongside its outbox synchronization record.
/// <see cref="SaleKind"/> distinguishes a scan-composed, catalog-backed sale
/// (`"Scanned"`) from the manual-total fallback (`"Manual"`, the default) —
/// commerce-pricing-engine design.md "POS: two explicit buttons, not a mode
/// toggle". Defaults to `"Manual"` so every pre-existing call site (and every
/// sale committed before this change shipped) keeps its original meaning
/// without a data migration.
/// </summary>
public sealed record SaleEffect(
    Guid SaleId,
    Guid BranchId,
    decimal TotalAmount,
    DateTimeOffset OccurredAtUtc,
    string SaleKind = "Manual",
    Guid? CustomerId = null,
    decimal? SaleDiscountPercent = null,
    decimal? SaleDiscountAmount = null,
    DiscountAuthorization? DiscountAuthorization = null,
    SaleTender? Tender = null,
    Guid? CashSessionId = null);

/// <summary>
/// One line of a scan-composed sale (commerce-pricing-engine design.md
/// "POS scan-to-sell"). Persisted only for `SaleKind == "Scanned"` sales; a
/// manual-total sale has zero rows here, which is exactly what distinguishes
/// the two kinds in the record beyond the `sale_kind` column itself.
/// <see cref="LineTotal"/> stays the UNDISCOUNTED amount (unit price times
/// quantity) so older readers keep its meaning; a discounted line also carries
/// its percentage and rounded amount, and nets <c>LineTotal - LineDiscountAmount</c>.
/// Both discount fields are null when the line has no discount.
/// </summary>
public sealed record SaleLine(
    Guid SaleId,
    int LineNumber,
    Guid PresentationId,
    string? IdentificationCode,
    string ProductName,
    string PresentationName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal? LineDiscountPercent = null,
    decimal? LineDiscountAmount = null);
