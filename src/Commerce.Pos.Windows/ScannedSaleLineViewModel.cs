using System.Globalization;

namespace Commerce.Pos.Windows;

/// <summary>
/// One row of the scan-composed sale <c>ListView</c> (commerce-pricing-engine
/// design.md "POS: two explicit buttons, not a mode toggle"). Carries the
/// already-resolved (<see cref="Commerce.Application.Pricing.PricingResolutionService"/>)
/// unit-net price and line total — this view model never computes a price
/// itself, only formats one that was already resolved.
/// </summary>
public sealed record ScannedSaleLineViewModel(
    Guid PresentationId,
    string? IdentificationCode,
    string ProductName,
    string PresentationName,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal)
{
    public string DisplayName => $"{ProductName} — {PresentationName}";

    public string QuantityText => Quantity.ToString("0.##", CultureInfo.InvariantCulture);

    public string UnitPriceText => UnitPrice.ToString("C", CultureInfo.CurrentCulture);

    public string LineTotalText => LineTotal.ToString("C", CultureInfo.CurrentCulture);
}
