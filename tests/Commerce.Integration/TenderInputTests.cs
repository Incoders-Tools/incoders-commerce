using System.Globalization;
using Commerce.Domain.Sales;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Tender Recorded at the Moment of Sale", POS side: what
/// the cash prompt accepts, the change it shows, the exact-amount fill, and the
/// Spanish labels of the tender.
/// </summary>
public sealed class TenderInputTests
{
    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("1000", "1000", "145")]
    [InlineData("1000.00", "1000.00", "145.00")]
    [InlineData("1000,50", "1000.50", "145.50")]   // Spanish keyboards type a decimal comma
    [InlineData(" 855 ", "855", "0")]
    [InlineData("$ 1000", "1000", "145")]
    public void EvaluateCash_ValidAmount_ReportsTheReceivedAmountAndTheChange(string text, string received, string change)
    {
        var entry = TenderInput.EvaluateCash(D("855"), text);

        Assert.True(entry.IsValid);
        Assert.Equal(D(received), entry.Received);
        Assert.Equal(D(change), entry.Change);
        Assert.Null(entry.Message);
    }

    [Theory]
    [InlineData("800")]
    [InlineData("854.99")]
    public void EvaluateCash_LessThanTheTotal_IsRefusedWithAMessage(string text)
    {
        var entry = TenderInput.EvaluateCash(D("855"), text);

        Assert.False(entry.IsValid);
        Assert.Null(entry.Change);
        Assert.Contains("menor", entry.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.000,50")]   // no thousands separator is ever accepted
    [InlineData("1000.005")]
    [InlineData("-5")]
    [InlineData("1e3")]
    public void EvaluateCash_NotAValidAmount_IsRefusedWithAMessage(string text)
    {
        var entry = TenderInput.EvaluateCash(D("855"), text);

        Assert.False(entry.IsValid);
        Assert.False(string.IsNullOrEmpty(entry.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EvaluateCash_NothingTypedYet_IsNotValidButShowsNoError(string? text)
    {
        var entry = TenderInput.EvaluateCash(D("855"), text);

        Assert.False(entry.IsValid);
        Assert.Null(entry.Message);
    }

    [Theory]
    [InlineData("855")]
    [InlineData("10.05")]
    [InlineData("9409.75")]
    [InlineData("0")]
    public void ExactText_FillsTheTotal_SoTheChangeIsZero(string total)
    {
        var entry = TenderInput.EvaluateCash(D(total), TenderInput.ExactText(D(total)));

        Assert.True(entry.IsValid);
        Assert.Equal(D(total), entry.Received);
        Assert.Equal(0m, entry.Change);
    }

    [Theory]
    [InlineData("cash", "Efectivo")]
    [InlineData("card", "Tarjeta")]
    [InlineData("qr", "QR")]
    public void Label_IsTheSpanishNameOfTheMethod(string method, string expected) =>
        Assert.Equal(expected, TenderInput.Label(method));

    [Fact]
    public void Describe_Cash_MentionsTheReceivedAmountAndTheChange()
    {
        SaleTenderRules.TryCash(855m, 1000m, out var tender);

        var text = TenderInput.Describe(tender);

        Assert.Contains("Efectivo", text);
        Assert.Contains("recibido", text);
        Assert.Contains("vuelto", text);
        Assert.Contains(145m.ToString("C", CultureInfo.CurrentCulture), text);
    }

    [Theory]
    [InlineData("card", "Tarjeta")]
    [InlineData("qr", "QR")]
    public void Describe_CardOrQr_HasNoCashDetails(string method, string label)
    {
        var text = TenderInput.Describe(new SaleTender(method));

        Assert.Equal(label, text);
    }
}
