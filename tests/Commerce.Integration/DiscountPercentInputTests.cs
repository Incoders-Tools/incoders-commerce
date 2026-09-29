using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The percentage the cashier types in the discount prompt: Spanish keyboards
/// type a decimal comma, and only a valid discount percentage is accepted.
/// </summary>
public sealed class DiscountPercentInputTests
{
    [Theory]
    [InlineData("10", "10")]
    [InlineData(" 12,5 ", "12.5")]
    [InlineData("12.5", "12.5")]
    [InlineData("100", "100")]
    [InlineData("0,01", "0.01")]
    [InlineData("7%", "7")]
    public void TryParse_AcceptsValidPercentages(string text, string expected)
    {
        Assert.True(DiscountPercentInput.TryParse(text, out var percent));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100,5")]
    [InlineData("12,345")]
    [InlineData("1.000,50")]
    public void TryParse_RefusesEverythingElse(string text) =>
        Assert.False(DiscountPercentInput.TryParse(text, out _));
}
