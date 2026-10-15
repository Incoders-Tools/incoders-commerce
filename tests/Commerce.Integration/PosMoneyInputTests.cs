using System.Text.RegularExpressions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Owner feedback 2026-10-15 round 2 (S3): one money rule for the whole POS, the web's <c>readMoney</c>. A point followed
/// by exactly three digits groups thousands ("15.000" opened the cash with 15); a comma, or a point followed by one or
/// two digits, is the decimal; anything else is refused. The open-cash prompt shows an example and the amount as read.
/// </summary>
public sealed class PosMoneyInputTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Src(string file) => File.ReadAllText(Path.Combine(Root(), "src", "Commerce.Pos.Windows", file));

    [Theory]
    [InlineData("15.000", "15000")]
    [InlineData("1.234.567,50", "1234567.50")]
    [InlineData("15.000,5", "15000.5")]
    [InlineData("15,5", "15.5")]
    [InlineData("15,50", "15.50")]
    [InlineData("15.50", "15.50")]
    [InlineData("15.5", "15.5")]
    [InlineData("15000", "15000")]
    [InlineData("0", "0")]
    [InlineData(" $ 15.000 ", "15000")]
    [InlineData("$15000,5", "15000.5")]
    public void Read_FollowsTheSharedRule(string text, string expected)
    {
        var reading = MoneyInput.Read(text);

        Assert.True(reading.IsValid);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), reading.Amount);
        Assert.Equal(MoneyReadFailure.None, reading.Failure);
    }

    [Theory]
    [InlineData("1,234.56")]
    [InlineData("15.0000")]
    [InlineData("1234.567")]
    [InlineData("1.234.5")]
    [InlineData("15,")]
    [InlineData(",5")]
    [InlineData("-15")]
    [InlineData("1e3")]
    [InlineData("abc")]
    public void Read_RefusesAnythingElse(string text)
    {
        var reading = MoneyInput.Read(text);

        Assert.False(reading.IsValid);
        Assert.Null(reading.Amount);
        Assert.Equal(MoneyReadFailure.Format, reading.Failure);
    }

    [Fact]
    public void Read_RefusesMoreDecimalsThanTheFieldTakes_AndTellsBlankApart()
    {
        Assert.Equal(MoneyReadFailure.Decimals, MoneyInput.Read("15,505").Failure);
        Assert.Equal(MoneyReadFailure.Blank, MoneyInput.Read("").Failure);
        Assert.Equal(MoneyReadFailure.Blank, MoneyInput.Read("  $ ").Failure);
        Assert.Equal(MoneyReadFailure.Blank, MoneyInput.Read(null).Failure);
    }

    [Fact]
    public void Placeholder_IsTheWebsExample()
    {
        Assert.Equal("Ej.: 15.000 o 15.000,50", MoneyInput.Placeholder);
    }

    [Fact]
    public void EveryPosReader_TakesFifteenThousand()
    {
        Assert.Equal(15_000m, CashSessionInput.ReadAmount("15.000").Amount);
        Assert.True(CashSessionInput.ReadAmount("15.000").IsValid);
        Assert.Equal(15_000m, CashSessionInput.EvaluateClose(0m, "15.000").Counted);
        Assert.Equal(15_000m, TenderInput.EvaluateCash(10_000m, "15.000").Received);
        // Cash movements and customer payments read through CustomerPaymentInput.TryReadAmount.
        Assert.Equal(15_000m, CashMovementInput.Evaluate(Commerce.Domain.CashSessions.CashMovement.Deposit, "15.000", "Cambio", 0m).Amount);
        Assert.Equal(15_000m, EmployeeFormRules.ParseAmount("15.000"));
    }

    [Fact]
    public void OpenCash_SaysWithWhatItWillOpen()
    {
        Assert.Equal("Se abrirá con $ 15.000,00", CashSessionInput.OpeningReadBack("15.000")?.Replace('\u00a0', ' '));
        Assert.Equal("Se abrirá con $ 0,00", CashSessionInput.OpeningReadBack("0")?.Replace('\u00a0', ' '));
        Assert.Null(CashSessionInput.OpeningReadBack(""));
        Assert.Null(CashSessionInput.OpeningReadBack("1,234.56"));
    }

    [Fact]
    public void OpenCashWindow_ShowsTheExampleWhileEmpty_AndTheAmountAsRead()
    {
        var xaml = Src("OpenCashWindow.xaml");
        var code = Src("OpenCashWindow.xaml.cs");

        var box = Regex.Match(xaml, @"<TextBox[^>]*x:Name=""OpeningFloatTextBox""[^>]*/>").Value;
        Assert.DoesNotContain("Text=\"0\"", box);
        Assert.Contains("x:Name=\"OpeningFloatPlaceholder\"", xaml);
        Assert.Contains("IsHitTestVisible=\"False\"", Regex.Match(xaml, @"<TextBlock[^>]*x:Name=""OpeningFloatPlaceholder""[^>]*/>").Value);
        Assert.Contains("x:Name=\"OpeningReadBackText\"", xaml);

        Assert.Contains("MoneyInput.Placeholder", code);
        Assert.Contains("CashSessionInput.OpeningReadBack(", code);
    }
}
