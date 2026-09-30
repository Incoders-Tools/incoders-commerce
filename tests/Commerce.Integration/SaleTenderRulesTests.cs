using System.Globalization;
using Commerce.Domain.Sales;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Tender Recorded at the Moment of Sale": cash needs an
/// amount received of at least the total with at most two decimals, the change
/// is received minus total, card and QR carry no cash fields.
/// </summary>
public sealed class SaleTenderRulesTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("855.00", "1000.00", "145.00")]
    [InlineData("855.00", "855.00", "0.00")]
    [InlineData("0.00", "0", "0")]
    [InlineData("10.05", "20", "9.95")]
    public void Cash_AtLeastTheTotal_RecordsTheReceivedAmountAndTheChange(string total, string received, string change)
    {
        Assert.True(SaleTenderRules.TryCash(D(total), D(received), out var tender));

        Assert.Equal(SaleTender.Cash, tender.Method);
        Assert.Equal(D(received), tender.AmountReceived);
        Assert.Equal(D(change), tender.ChangeGiven);
    }

    [Theory]
    [InlineData("855.00", "800.00")]
    [InlineData("855.00", "854.99")]
    [InlineData("855.00", "-1")]
    [InlineData("855.00", "1000.001")]
    [InlineData("855.00", "1000.005")]
    public void Cash_LessThanTheTotalOrWithMoreThanTwoDecimals_IsRefused(string total, string received)
    {
        Assert.False(SaleTenderRules.TryCash(D(total), D(received), out var tender));
        Assert.Null(tender);
    }

    [Fact]
    public void Card_And_Qr_CarryNoAmountReceivedAndNoChange()
    {
        var card = SaleTenderRules.Card();
        var qr = SaleTenderRules.Qr();

        Assert.Equal("card", card.Method);
        Assert.Equal("qr", qr.Method);
        Assert.Null(card.AmountReceived);
        Assert.Null(card.ChangeGiven);
        Assert.Null(qr.AmountReceived);
        Assert.Null(qr.ChangeGiven);
    }

    [Theory]
    [InlineData("cash", true)]
    [InlineData("card", true)]
    [InlineData("qr", true)]
    [InlineData("MercadoPago", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnownMethod_OnlyTheThreeTenderMethods(string? method, bool expected) =>
        Assert.Equal(expected, SaleTender.IsKnownMethod(method));
}
