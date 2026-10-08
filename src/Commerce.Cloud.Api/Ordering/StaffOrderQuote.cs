using Commerce.Cloud.Api.Endpoints;
using Commerce.Domain.Ordering;

namespace Commerce.Cloud.Api.Ordering;

/// <summary>
/// The price preview of a staff-entered order (staff-order-taking T2, <c>POST /orders/staff/quote</c>). Nothing is
/// stored. <see cref="Status"/> is <c>quoted</c> when every line is priced, otherwise <c>denied</c> with
/// <see cref="Reason"/> <c>not-found</c> / <c>customer-disabled</c> (no lines) or <c>no-effective-price</c> (the lines
/// say which). <see cref="PriceListId"/>/<see cref="PriceListName"/> are the buyer's list; <see cref="Total"/> sums the
/// priced lines.
/// </summary>
public sealed record StaffOrderQuote(
    string Status,
    string? Reason,
    Guid CustomerId,
    Guid? PriceListId,
    string? PriceListName,
    decimal? DiscountPercentage,
    IReadOnlyList<StaffOrderQuoteLine> Lines,
    decimal Total)
{
    public const string QuotedStatus = "quoted";
    public const string DeniedStatus = "denied";

    public static StaffOrderQuote Denied(string reason, Guid customerId) =>
        new(DeniedStatus, reason, customerId, PriceListId: null, PriceListName: null, DiscountPercentage: null, [], 0m);
}

/// <summary>
/// One quoted line. <see cref="Status"/> is <c>priced</c> (every price field set) or <c>no-effective-price</c> (only the
/// request echo). <see cref="PriceListId"/> is the list that priced the line and <see cref="FellBack"/> is true when the
/// customer's list had no price and the organization default list priced it instead.
/// </summary>
public sealed record StaffOrderQuoteLine(
    Guid ProductId,
    Guid PresentationId,
    decimal Quantity,
    string Status,
    string? ProductName,
    string? PresentationName,
    string? QuantityBehavior,
    decimal? UnitListPrice,
    decimal? AppliedDiscountPercentage,
    decimal? UnitNetPrice,
    decimal? LineTotal,
    Guid? PriceListId,
    string? PriceListName,
    bool FellBack)
{
    public const string PricedStatus = "priced";
    public const string NoEffectivePriceStatus = "no-effective-price";

    public static StaffOrderQuoteLine Priced(OrderLineSnapshot line, string? priceListName) => new(
        line.ProductId, line.PresentationId, line.Quantity, PricedStatus, line.ProductName, line.PresentationName,
        line.QuantityBehavior.ToString(), line.UnitListPrice, line.AppliedDiscountPercentage, line.UnitNetPrice, line.LineTotal,
        line.PricedFromListId, priceListName, line.FellBack);

    public static StaffOrderQuoteLine Unpriced(SubmitOrderLine line) => new(
        line.ProductId, line.PresentationId, line.Quantity, NoEffectivePriceStatus, null, null, null, null, null, null, null,
        null, null, FellBack: false);
}
