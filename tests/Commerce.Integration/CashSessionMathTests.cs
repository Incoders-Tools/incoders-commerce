using Commerce.Domain.CashSessions;
using Commerce.Domain.Sales;

namespace Commerce.Integration;

/// <summary>pos-cash-session "Closing a Cash Session": pure totals, expected cash and difference.</summary>
public sealed class CashSessionMathTests
{
    [Fact]
    public void Summarize_SplitsTotalsByTender_AndCountsCashActuallyKept()
    {
        SaleTenderRules.TryCash(855m, 1000m, out var cashWithChange);
        SaleTenderRules.TryCash(100m, 100m, out var exactCash);

        var summary = CashSessionMath.Summarize(5000m,
        [
            new CashSessionSale(855m, cashWithChange),
            new CashSessionSale(100m, exactCash),
            new CashSessionSale(300m, SaleTenderRules.Card()),
            new CashSessionSale(200m, SaleTenderRules.Qr()),
        ]);

        Assert.Equal(4, summary.SaleCount);
        Assert.Equal(955m, summary.CashKept);
        Assert.Equal(300m, summary.CardTotal);
        Assert.Equal(200m, summary.QrTotal);
        Assert.Equal(0m, summary.UntenderedTotal);
        Assert.Equal(5955m, summary.ExpectedCash);
    }

    [Fact]
    public void Summarize_WithNoSales_ExpectsJustTheFloat()
    {
        var summary = CashSessionMath.Summarize(2000m, []);

        Assert.Equal(0, summary.SaleCount);
        Assert.Equal(0m, summary.CashKept);
        Assert.Equal(2000m, summary.ExpectedCash);
    }

    [Fact]
    public void Summarize_CountsASaleWithoutATenderButKeepsItOutOfEveryMethodTotal()
    {
        var summary = CashSessionMath.Summarize(0m, [new CashSessionSale(500m, null)]);

        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(500m, summary.UntenderedTotal);
        Assert.Equal(0m, summary.ExpectedCash);
    }

    [Fact]
    public void Summarize_UsesTheSaleTotalForCashWithoutAmounts()
    {
        var summary = CashSessionMath.Summarize(0m, [new CashSessionSale(250m, new SaleTender(SaleTender.Cash))]);

        Assert.Equal(250m, summary.CashKept);
    }

    [Theory]
    [InlineData(5900, 5955, -55)]
    [InlineData(5955, 5955, 0)]
    [InlineData(6000, 5955, 45)]
    public void Difference_IsCountedMinusExpected(double counted, double expected, double difference) =>
        Assert.Equal((decimal)difference, CashSessionMath.Difference((decimal)counted, (decimal)expected));

    [Theory]
    [InlineData("0", true)]
    [InlineData("5000.50", true)]
    [InlineData("-1", false)]
    [InlineData("10.005", false)]
    public void IsValidAmount_AcceptsZeroOrMoreWithAtMostTwoDecimals(string amount, bool expected) =>
        Assert.Equal(expected, CashSessionMath.IsValidAmount(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
}
