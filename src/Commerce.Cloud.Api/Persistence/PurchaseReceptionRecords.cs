using System.Text.Json.Serialization;
using Commerce.Domain.Purchasing;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>One line of a reception being saved. `LotCode` and `ExpiresOn` are optional.</summary>
public sealed record NewReceptionLine(Guid PresentationId, decimal Quantity, decimal UnitCost, string? LotCode, DateOnly? ExpiresOn);

/// <summary>The editable content of a DRAFT reception (create and replace-on-update share it).</summary>
public sealed record ReceptionContent(
    Guid SupplierId,
    ReceptionDocumentType DocumentType,
    string? DocumentReference,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    string? Notes,
    IReadOnlyList<NewReceptionLine> Lines);

public sealed record ReceptionLineRecord(
    Guid Id,
    Guid PresentationId,
    string ProductName,
    string PresentationName,
    string QuantityBehavior,
    decimal Quantity,
    decimal UnitCost,
    decimal LineTotal,
    string? LotCode,
    DateOnly? ExpiresOn,
    int SortOrder);

/// <summary>A reception with its lines, as returned by every receptions endpoint that returns one.</summary>
public sealed record ReceptionRecord(
    Guid Id,
    Guid BranchId,
    Guid SupplierId,
    string SupplierName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReceptionStatus Status,
    string? Number,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReceptionDocumentType DocumentType,
    string? DocumentReference,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    string? Notes,
    decimal TotalAmount,
    Guid? LedgerInvoiceMovementId,
    Guid? LedgerReversalMovementId,
    string? VoidReason,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset? VoidedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ReceptionLineRecord> Lines);

/// <summary>A row of the receptions list (no lines).</summary>
public sealed record ReceptionSummaryRecord(
    Guid Id,
    Guid SupplierId,
    string SupplierName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReceptionStatus Status,
    string? Number,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ReceptionDocumentType DocumentType,
    string? DocumentReference,
    DateOnly OccurredOn,
    DateOnly? DueOn,
    decimal TotalAmount,
    int LineCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReceptionListFilter(
    ReceptionStatus? Status, Guid? SupplierId, DateOnly? From, DateOnly? To, string? Search);

public enum ReceptionWriteOutcome
{
    Saved,
    NotFound,
    NotDraft,
    Modified,
    SupplierNotFound,
    InvalidLine,
}

/// <summary>`Field`/`Message` describe the first invalid line (`lines[i].quantity`, `lines[i].presentationId`, ...) or the supplier.</summary>
public sealed record ReceptionWriteResult(
    ReceptionWriteOutcome Outcome, ReceptionRecord? Reception = null, string? Field = null, string? Message = null);

public enum ReceptionConfirmOutcome
{
    Confirmed,
    NotFound,
    NotDraft,
    NoLines,
    DuplicateDocument,
}

public sealed record ReceptionConfirmResult(ReceptionConfirmOutcome Outcome, ReceptionRecord? Reception = null);

public enum ReceptionVoidOutcome
{
    Voided,
    NotFound,
    NotConfirmed,
}

public sealed record ReceptionVoidResult(ReceptionVoidOutcome Outcome, ReceptionRecord? Reception = null);
