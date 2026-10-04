using System.Globalization;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// operator-ux-adjustments T3: the quantity rules of a sale line per quantity behavior. Pure (no WPF): weighted and bulk
/// lines carry up to three decimals (kilos), fixed-quantity lines whole units. The operator may type a decimal comma or a
/// decimal point; the line shows the number with the terminal culture's separator.
/// </summary>
public sealed class SaleQuantityTests
{
    private static readonly CultureInfo Comma = CultureInfo.GetCultureInfo("es-AR");
    private static readonly CultureInfo Dot = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData("Weighted", true)]
    [InlineData("Bulk", true)]
    [InlineData("FixedQuantity", false)]
    [InlineData("", false)]
    public void IsMeasured_OnlyForWeightedAndBulk(string behavior, bool measured)
    {
        Assert.Equal(measured, SaleQuantity.IsMeasured(behavior));
    }

    [Theory]
    [InlineData("0,550", 0.550)]
    [InlineData("0.550", 0.550)]
    [InlineData(" 1,5 ", 1.5)]
    [InlineData(",25", 0.25)]
    [InlineData("12", 12)]
    [InlineData("0,001", 0.001)]
    public void TryParse_Weighted_AcceptsCommaOrDot_UpToThreeDecimals(string text, double expected)
    {
        Assert.True(SaleQuantity.TryParse(text, "Weighted", out var quantity, out var error));
        Assert.Equal((decimal)expected, quantity);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0,000")]
    [InlineData("-1")]
    [InlineData("1,2345")]
    [InlineData("1.000,5")]
    [InlineData("1,000.5")]
    [InlineData("abc")]
    [InlineData("1e3")]
    public void TryParse_Weighted_RefusesEmptyZeroNegativeTooPreciseOrGarbage(string text)
    {
        Assert.False(SaleQuantity.TryParse(text, "Weighted", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_FixedQuantity_AcceptsWholeUnitsOnly()
    {
        Assert.True(SaleQuantity.TryParse("3", "FixedQuantity", out var quantity, out _));
        Assert.Equal(3m, quantity);
        Assert.False(SaleQuantity.TryParse("2,5", "FixedQuantity", out _, out var error));
        Assert.Contains("enteras", error);
    }

    [Fact]
    public void Validate_RefusesZeroOrNegative_AndPrecisionPerBehavior()
    {
        Assert.Null(SaleQuantity.Validate(0.555m, "Weighted"));
        Assert.NotNull(SaleQuantity.Validate(0m, "Weighted"));
        Assert.NotNull(SaleQuantity.Validate(-0.5m, "Bulk"));
        Assert.NotNull(SaleQuantity.Validate(0.5555m, "Weighted"));
        Assert.Null(SaleQuantity.Validate(2m, "FixedQuantity"));
        Assert.NotNull(SaleQuantity.Validate(1.5m, "FixedQuantity"));
    }

    [Fact]
    public void Text_Weighted_ShowsThreeDecimalsAndKg_WithTheCultureSeparator()
    {
        Assert.Equal("0,550 kg", SaleQuantity.Text(0.55m, "Weighted", Comma));
        Assert.Equal("0.550 kg", SaleQuantity.Text(0.55m, "Weighted", Dot));
        Assert.Equal("2,000 kg", SaleQuantity.Text(2m, "Weighted", Comma));
    }

    [Fact]
    public void Text_Bulk_ShowsThreeDecimalsWithoutAUnit_AndFixedShowsWholeUnits()
    {
        Assert.Equal("1,250", SaleQuantity.Text(1.25m, "Bulk", Comma));
        Assert.Equal("2", SaleQuantity.Text(2m, "FixedQuantity", Comma));
        Assert.Equal("1234", SaleQuantity.Text(1234m, "FixedQuantity", Dot));
    }

    [Fact]
    public void EditText_IsTheNumberWithoutUnit_AndParsesBackToTheSameQuantity()
    {
        var text = SaleQuantity.EditText(0.55m, "Weighted", Comma);

        Assert.Equal("0,550", text);
        Assert.True(SaleQuantity.TryParse(text, "Weighted", out var parsed, out _));
        Assert.Equal(0.55m, parsed);
    }

    [Fact]
    public void UnitPriceText_Weighted_IsPerKilo()
    {
        Assert.Equal(1000m.ToString("C", Comma) + "/kg", SaleQuantity.UnitPriceText(1000m, "Weighted", Comma));
        Assert.Equal(1000m.ToString("C", Comma), SaleQuantity.UnitPriceText(1000m, "FixedQuantity", Comma));
    }
}
