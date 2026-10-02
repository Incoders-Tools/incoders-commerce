using Commerce.Domain.Catalog;
using Commerce.Domain.Purchasing;
using Commerce.Domain.Stock;
using Commerce.Domain.Tenancy;

namespace Commerce.Integration;

/// <summary>Pure rules of goods receptions (totals, rounding, quantity precision, the R number) and of stock movements.</summary>
public sealed class PurchasingDomainTests
{
    [Theory]
    [InlineData(120, 4000, 480000)]
    [InlineData(2.5, 1.005, 2.51)]   // 2.5125 -> 2.51
    [InlineData(0.333, 3, 1.00)]     // 0.999 -> 1.00
    [InlineData(1, 0.0049, 0.00)]
    [InlineData(1, 0.0050, 0.01)]    // half away from zero
    public void LineTotal_IsQuantityTimesCost_RoundedToTwoDecimals(double quantity, double unitCost, double expected) =>
        Assert.Equal((decimal)expected, ReceptionRules.LineTotal((decimal)quantity, (decimal)unitCost));

    [Fact]
    public void Total_IsTheSumOfTheRoundedLineTotals() =>
        Assert.Equal(3.02m, ReceptionRules.Total([(0.333m, 3.0m), (1m, 2.02m)]));

    [Fact]
    public void Total_RoundsEachLineBeforeSumming() =>
        Assert.Equal(0.02m, ReceptionRules.Total([(1m, 0.0050m), (1m, 0.0050m)]));

    [Theory]
    [InlineData(QuantityBehavior.FixedQuantity, 3, true)]
    [InlineData(QuantityBehavior.FixedQuantity, 2.5, false)]
    [InlineData(QuantityBehavior.Weighted, 2.5, true)]
    [InlineData(QuantityBehavior.Weighted, 120.125, true)]
    [InlineData(QuantityBehavior.Weighted, 1.2345, false)]
    [InlineData(QuantityBehavior.Bulk, 0.001, true)]
    [InlineData(QuantityBehavior.Bulk, 0.0001, false)]
    [InlineData(QuantityBehavior.Weighted, 0, false)]
    [InlineData(QuantityBehavior.FixedQuantity, -1, false)]
    public void ReceivedQuantity_FollowsTheQuantityBehavior(QuantityBehavior behavior, double quantity, bool valid) =>
        Assert.Equal(valid, ReceptionRules.TryValidateQuantity(behavior, (decimal)quantity, out _));

    [Theory]
    [InlineData(0, true)]
    [InlineData(4000.1234, true)]
    [InlineData(1.00001, false)]
    [InlineData(-1, false)]
    public void UnitCost_IsNonNegative_WithAtMostFourDecimals(double cost, bool valid) =>
        Assert.Equal(valid, ReceptionRules.TryValidateUnitCost((decimal)cost, out _));

    [Fact]
    public void ReceptionNumber_IsRBranchWSequence()
    {
        var number = new ReceptionNumber(new BranchCode(1), 37);
        Assert.Equal("R01-W-37", number.Format());
        Assert.True(ReceptionNumber.TryParse("R12-W-410", out var parsed));
        Assert.Equal(new ReceptionNumber(new BranchCode(12), 410), parsed);
        Assert.False(ReceptionNumber.TryParse("P01-W-37", out _));
        Assert.False(ReceptionNumber.TryParse("R01-W-037", out _));
    }

    [Theory]
    [InlineData(StockMovementKind.Opening, 5, true)]
    [InlineData(StockMovementKind.Opening, -5, false)]
    [InlineData(StockMovementKind.Shrinkage, -1, true)]
    [InlineData(StockMovementKind.Shrinkage, 1, false)]
    [InlineData(StockMovementKind.CountCorrection, -3, true)]
    [InlineData(StockMovementKind.CountCorrection, 3, true)]
    [InlineData(StockMovementKind.Adjustment, 0, false)]
    [InlineData(StockMovementKind.PurchaseReceipt, 1, false)] // never registered by hand
    [InlineData(StockMovementKind.Sale, -1, false)]
    [InlineData(StockMovementKind.Reversal, 1, false)]
    public void ManualAdjustment_SignDependsOnTheKind(StockMovementKind kind, double quantity, bool valid) =>
        Assert.Equal(valid, StockRules.TryValidateAdjustment(kind, QuantityBehavior.Weighted, (decimal)quantity, out _));

    [Fact]
    public void ManualAdjustment_OfAFixedQuantityPresentation_MustBeAWholeNumber()
    {
        Assert.True(StockRules.TryValidateAdjustment(StockMovementKind.Opening, QuantityBehavior.FixedQuantity, 12, out _));
        Assert.False(StockRules.TryValidateAdjustment(StockMovementKind.Opening, QuantityBehavior.FixedQuantity, 1.5m, out _));
    }

    [Theory]
    [InlineData(5, 10, true)]
    [InlineData(10, 10, false)]
    [InlineData(-1, 0, true)]   // negative stock is flagged even with a 0 minimum
    public void BelowMinimum_NeedsAMinimum_AndStrictlyLessOnHand(double onHand, double minimum, bool expected) =>
        Assert.Equal(expected, StockRules.IsBelowMinimum((decimal)onHand, (decimal)minimum));

    [Fact]
    public void BelowMinimum_WithoutAMinimum_IsNeverBelow() => Assert.False(StockRules.IsBelowMinimum(-5m, null));

    [Theory]
    [InlineData(StockMovementKind.PurchaseReceipt, 1, true)]
    [InlineData(StockMovementKind.PurchaseReceipt, -1, false)]
    [InlineData(StockMovementKind.Sale, -2.5, true)]
    [InlineData(StockMovementKind.Sale, 2.5, false)]
    [InlineData(StockMovementKind.Reversal, -1, true)]
    [InlineData(StockMovementKind.Reversal, 1, true)]
    [InlineData(StockMovementKind.Reversal, 0, false)]
    public void SignRule_MirrorsTheDatabaseCheck(StockMovementKind kind, double quantity, bool valid) =>
        Assert.Equal(valid, StockRules.IsSignValid(kind, (decimal)quantity));
}
