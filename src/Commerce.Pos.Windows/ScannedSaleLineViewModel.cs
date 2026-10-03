using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>
/// One row of the scan-composed sale <c>ListView</c> (commerce-pricing-engine
/// design.md "POS: two explicit buttons, not a mode toggle"). Carries the
/// already-resolved (<see cref="Commerce.Application.Pricing.PricingResolutionService"/>)
/// unit-net price and line total — this view model never computes a price
/// itself, only formats one that was already resolved. <see cref="LineTotal"/>
/// is the UNDISCOUNTED amount; a discounted line also carries its percentage and
/// rounded amount (both null otherwise) and nets <see cref="NetTotal"/>.
/// </summary>
public sealed record ScannedSaleLineViewModel(
    Guid PresentationId,
    string? IdentificationCode,
    string ProductName,
    string PresentationName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    decimal? LineDiscountPercent = null,
    decimal? LineDiscountAmount = null,
    string? FallbackListName = null)
{
    public bool HasDiscount => LineDiscountPercent is not null;

    public decimal NetTotal => LineTotal - (LineDiscountAmount ?? 0m);

    public string NetTotalText => NetTotal.ToString("C", CultureInfo.CurrentCulture);

    public string DiscountText => LineDiscountPercent is { } percent
        ? $"-{percent.ToString("0.##", CultureInfo.CurrentCulture)}% ({(-(LineDiscountAmount ?? 0m)).ToString("C", CultureInfo.CurrentCulture)})"
        : string.Empty;

    /// <summary>customer-price-lists T6: a discreet "(precio de Mostrador)" when the buyer's list had no price and the default list priced this line; empty otherwise.</summary>
    public string PriceNote => FallbackListName is null ? string.Empty : $"(precio de {FallbackListName})";

    public string DisplayName => $"{ProductName} — {PresentationName}";

    public string QuantityText => Quantity.ToString("0.##", CultureInfo.InvariantCulture);

    public string UnitPriceText => UnitPrice.ToString("C", CultureInfo.CurrentCulture);

    public string LineTotalText => LineTotal.ToString("C", CultureInfo.CurrentCulture);
}
