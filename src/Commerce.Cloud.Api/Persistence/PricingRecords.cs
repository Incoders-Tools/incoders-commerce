namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresPriceListStore.CreatePriceListAsync"/>. No
/// `OrganizationId` (comes from the tenant scope, never a request field) —
/// commerce-pricing-engine design.md "Which price list resolves".
/// </summary>
public sealed record NewPriceList(Guid Id, string Name, bool IsDefault, Guid CreatedByUserId);

/// <summary>Full persisted shape of one `price_lists` row.</summary>
public sealed record PriceListRecord(
    Guid Id,
    Guid OrganizationId,
    string Name,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId);

/// <summary>
/// Input to <see cref="PostgresPriceListStore.AppendEntryAsync"/> — a NEW
/// price publication, never an edit of an existing one (design.md
/// "Effective-dating shape": append-only, no `EffectiveTo`).
/// </summary>
public sealed record NewPriceListEntry(
    Guid Id,
    Guid PriceListId,
    Guid PresentationId,
    decimal UnitPrice,
    DateOnly EffectiveFrom,
    string Source,
    Guid? ImportBatchId,
    Guid CreatedByUserId);

/// <summary>Full persisted shape of one `price_list_entries` row.</summary>
public sealed record PriceListEntryRecord(
    Guid Id,
    Guid OrganizationId,
    Guid PriceListId,
    Guid PresentationId,
    decimal UnitPrice,
    DateOnly EffectiveFrom,
    string Source,
    Guid? ImportBatchId,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId);

// --- Supplier price import (commerce-pricing-engine design.md "Import
// state machine and untrusted-file handling", Work Unit 9) -----------------

/// <summary>
/// Input to <see cref="PostgresPriceListStore.CreateSupplierMappingAsync"/> —
/// design.md "Per-supplier column mapping": sheet name, header row, and
/// price/code COLUMN LETTERS (not names), saved once per supplier and
/// reused for every later import from that supplier.
/// </summary>
public sealed record NewSupplierPriceMapping(
    Guid Id, string SupplierName, string SheetName, int HeaderRow, string CodeColumn, string PriceColumn, Guid CreatedByUserId);

/// <summary>Full persisted shape of one `supplier_price_mappings` row.</summary>
public sealed record SupplierPriceMappingRecord(
    Guid Id,
    Guid OrganizationId,
    string SupplierName,
    string SheetName,
    int HeaderRow,
    string CodeColumn,
    string PriceColumn,
    DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId);

/// <summary>One matched row, ready to persist as a `price_import_rows` insert (input side of the batch write).</summary>
public sealed record NewImportBatchRow(
    int RowNumber,
    string? RawCode,
    string? RawPrice,
    Guid? PresentationId,
    decimal? CurrentPrice,
    decimal? ProposedPrice,
    string MatchStatus);

/// <summary>Full persisted shape of one `price_import_rows` row — the review table's source.</summary>
public sealed record ImportBatchRowRecord(
    Guid Id,
    Guid OrganizationId,
    Guid BatchId,
    int RowNumber,
    string? RawCode,
    string? RawPrice,
    Guid? PresentationId,
    decimal? CurrentPrice,
    decimal? ProposedPrice,
    string MatchStatus,
    string? RejectReason);

/// <summary>
/// Full persisted shape of one `price_import_batches` row. `Status` is one
/// of `Staged` (design.md: deliberately NO `Uploaded` state) `| Committed |
/// Rejected | Failed`.
/// </summary>
public sealed record ImportBatchRecord(
    Guid Id,
    Guid OrganizationId,
    Guid SupplierMappingId,
    string FileName,
    int RowCount,
    string Status,
    DateTimeOffset UploadedAtUtc,
    Guid UploadedByUserId,
    DateTimeOffset? ResolvedAtUtc);

/// <summary>
/// Thrown by <see cref="PostgresPriceListStore.CommitImportBatchAsync"/> /
/// <c>RejectImportBatchAsync</c> when the batch is not currently `Staged` —
/// the caller (endpoint) translates this into a 409, never a silent no-op.
/// </summary>
public sealed class ImportBatchNotStagedException(Guid batchId, string actualStatus)
    : Exception($"Import batch {batchId} is '{actualStatus}', not 'Staged'.")
{
    public Guid BatchId { get; } = batchId;
    public string ActualStatus { get; } = actualStatus;
}
