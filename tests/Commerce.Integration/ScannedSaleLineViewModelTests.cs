using System.Globalization;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 7 task 7.4: pure formatting for the
/// scan-composed sale <c>ListView</c> row. No I/O, no WPF, no price
/// computation — the view model only formats an already-resolved price.
/// </summary>
public sealed class ScannedSaleLineViewModelTests
{
    [Fact]
    public void DisplayName_CombinesProductAndPresentationName()
    {
        var line = new ScannedSaleLineViewModel(Guid.NewGuid(), "7791234567890", "Flour", "1kg Bag", 2m, 100m, 200m);

        Assert.Equal("Flour — 1kg Bag", line.DisplayName);
        Assert.Equal("2", line.QuantityText);
        // Currency formatting follows CultureInfo.CurrentCulture (matching
        // the existing SaleResultText ":C" precedent) rather than a
        // hardcoded literal, so this test is not tied to en-US.
        Assert.Equal(100m.ToString("C", CultureInfo.CurrentCulture), line.UnitPriceText);
        Assert.Equal(200m.ToString("C", CultureInfo.CurrentCulture), line.LineTotalText);
    }
}
