using Commerce.Domain.CashSessions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The pure logic behind the open-cash and close-cash prompts
/// (pos-cash-session "Cash Session Operator Interface"): reading the float and
/// the counted cash typed with a decimal comma, the live difference, and the
/// Spanish labels.
/// </summary>
public sealed class CashSessionInputTests
{
    [Theory]
    [InlineData("5000", 5000)]
    [InlineData("5000,50", 5000.5)]
    [InlineData("5000.50", 5000.5)]
    [InlineData(" $ 2000 ", 2000)]
    [InlineData("0", 0)]
    public void ReadAmount_AcceptsZeroOrMoreWithADecimalCommaOrPoint(string text, double expected)
    {
        var entry = CashSessionInput.ReadAmount(text);

        Assert.True(entry.IsValid);
        Assert.Equal((decimal)expected, entry.Amount);
        Assert.Null(entry.Message);
    }

    [Fact]
    public void ReadAmount_StaysQuietWhileNothingIsTyped()
    {
        var entry = CashSessionInput.ReadAmount("  ");

        Assert.False(entry.IsValid);
        Assert.Null(entry.Amount);
        Assert.Null(entry.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("10.005")]
    [InlineData("1,000.00")]
    public void ReadAmount_RefusesAnythingElseWithASpanishMessage(string text)
    {
        var entry = CashSessionInput.ReadAmount(text);

        Assert.False(entry.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(entry.Message));
    }

    [Fact]
    public void EvaluateClose_ShowsTheLiveDifferenceAgainstTheExpectedCash()
    {
        var short_ = CashSessionInput.EvaluateClose(5955m, "5900");
        var over = CashSessionInput.EvaluateClose(5955m, "6000,00");
        var exact = CashSessionInput.EvaluateClose(5955m, "5955");

        Assert.True(short_.IsValid);
        Assert.Equal(-55m, short_.Difference);
        Assert.Equal(45m, over.Difference);
        Assert.Equal(0m, exact.Difference);
    }

    [Fact]
    public void EvaluateClose_HasNoDifferenceUntilTheAmountIsReadable()
    {
        Assert.Null(CashSessionInput.EvaluateClose(5955m, "").Difference);
        Assert.Null(CashSessionInput.EvaluateClose(5955m, "abc").Difference);
        Assert.False(CashSessionInput.EvaluateClose(5955m, "-1").IsValid);
    }

    [Theory]
    [InlineData(-55, "Faltante")]
    [InlineData(45, "Sobrante")]
    [InlineData(0, "Sin diferencia")]
    public void DifferenceLabel_NamesShortageSurplusOrNoDifference(int difference, string expectedStart) =>
        Assert.StartsWith(expectedStart, CashSessionInput.DifferenceLabel(difference));

    [Fact]
    public void HeaderText_ShowsTheSessionStateAndTheLocalOpeningTime()
    {
        var localOpening = new DateTime(2026, 9, 30, 8, 15, 0, DateTimeKind.Local);
        var session = new CashSession(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(localOpening).ToUniversalTime(), 5000m);

        Assert.Equal("Caja abierta · 08:15", CashSessionInput.HeaderText(session));
        Assert.Equal("Caja cerrada", CashSessionInput.HeaderText(null));
    }
}
