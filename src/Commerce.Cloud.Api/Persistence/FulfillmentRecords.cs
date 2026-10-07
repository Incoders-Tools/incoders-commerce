namespace Commerce.Cloud.Api.Persistence;

/// <summary>One order in the tracking list of the selected branch.</summary>
public sealed record OrderTrackingSummary(
    Guid OrderId,
    string OrderNumber,
    DateTimeOffset SubmittedAtUtc,
    string Origin,
    Guid? CustomerId,
    string CustomerName,
    string Status,
    decimal Total,
    int LineCount,
    Guid? RunId,
    int? RunNumber,
    DateOnly? RunDate,
    string? RemitoNumber,
    decimal? DeliveredTotal,
    string? Settlement,
    DateTimeOffset? DeliveredAtUtc,
    string? Note,
    string? CancelReason);

/// <summary>One order line, with what was delivered once the order came back from its run (null before).</summary>
public sealed record OrderTrackingLine(
    int LineNo,
    Guid PresentationId,
    string ProductName,
    string PresentationName,
    string QuantityBehavior,
    decimal Quantity,
    decimal UnitNetPrice,
    decimal LineTotal,
    decimal? DeliveredQuantity);

/// <summary>Who an order is for, as a remito prints it. Address parts are null when unknown.</summary>
public sealed record OrderPartyData(
    string DisplayName,
    string? LegalName,
    string? TaxIdType,
    string? TaxId,
    string? TaxCondition,
    string? Phone,
    string? Address,
    string? Locality,
    string? Province,
    string? PostalCode,
    string? DeliveryNotes);

public sealed record OrderTrackingDetail(
    OrderTrackingSummary Summary,
    IReadOnlyList<OrderTrackingLine> Lines,
    OrderPartyData Party,
    IReadOnlyList<string> AllowedTransitions);

/// <summary>A delivery run (reparto) of the selected branch.</summary>
public sealed record DeliveryRunSummary(
    Guid RunId,
    int RunNumber,
    DateOnly RunDate,
    string? DriverName,
    string? Vehicle,
    string? Notes,
    string Status,
    int OrderCount,
    decimal Total,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DispatchedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record DeliveryRunStop(int StopNo, OrderTrackingSummary Order);

public sealed record DeliveryRunDetail(DeliveryRunSummary Run, IReadOnlyList<DeliveryRunStop> Stops);

/// <summary>The organization's data printed on its documents (all optional but the name).</summary>
public sealed record OrganizationDocumentProfile(
    string Name,
    string? LegalName,
    string? TaxId,
    string? TaxCondition,
    string? GrossIncomeNumber,
    DateOnly? ActivityStartDate,
    string? FiscalAddress,
    string? DocumentFooter,
    string? LogoUrl,
    string? PrimaryColor);

/// <summary>A branch's data printed on its documents (all optional but the name and code).</summary>
public sealed record BranchDocumentProfile(
    Guid BranchId,
    string Name,
    int Code,
    string? Address,
    string? Locality,
    string? Phone,
    string? Email,
    string? WarehouseAddress);

/// <summary>One remito (delivery note) ready to print: who issues it, for whom, what is delivered and its value.</summary>
public sealed record RemitoDocument(
    Guid OrderId,
    string RemitoNumber,
    string OrderNumber,
    DateOnly IssuedOn,
    OrganizationDocumentProfile Organization,
    BranchDocumentProfile Branch,
    OrderPartyData Customer,
    IReadOnlyList<OrderTrackingLine> Lines,
    decimal Total,
    int? RunNumber,
    string? DriverName,
    string? Vehicle,
    string? Note);

/// <summary>How one order of a run came back: per line delivered quantities (missing lines = as ordered) and settlement.</summary>
public sealed record OrderReturnInput(
    Guid OrderId,
    bool Delivered,
    string? Settlement,
    IReadOnlyList<LineDeliveryInput>? Lines);

public sealed record LineDeliveryInput(int LineNo, decimal DeliveredQuantity);

public enum FulfillmentOutcome
{
    Done,
    NotFound,
    InvalidTransition,
    Conflict,
    Invalid,
}

public sealed record FulfillmentResult(FulfillmentOutcome Outcome, string? Error = null, Guid? Id = null)
{
    public static FulfillmentResult Ok(Guid? id = null) => new(FulfillmentOutcome.Done, null, id);

    public static FulfillmentResult NotFound() => new(FulfillmentOutcome.NotFound);

    public static FulfillmentResult Conflict(string error) => new(FulfillmentOutcome.Conflict, error);

    public static FulfillmentResult Invalid(string error) => new(FulfillmentOutcome.Invalid, error);
}
