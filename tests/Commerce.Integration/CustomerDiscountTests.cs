using System.Text.RegularExpressions;
using Commerce.Domain.Customers;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// odd/tasks/owner-requests-quick-adjustments.md Q3: the customer's discount is a percentage from 0 to 100 with up to
/// two decimals, enforced on the server, read the same way in the POS (a decimal comma or point, an optional "%", no
/// thousands separator: "7,5" is 7.5, never 75) and explained next to the field in both layers.
/// Q1 rides along: the existing delivery notes field is the location reference ("Referencias de ubicación").
/// </summary>
public sealed class CustomerDiscountTests
{
    // ---- domain rule ----------------------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData(0.0, true)]
    [InlineData(7.5, true)]
    [InlineData(12.25, true)]
    [InlineData(100.0, true)]
    [InlineData(-1.0, false)]
    [InlineData(100.01, false)]
    [InlineData(7.555, false)]
    [InlineData(75.0, true)]
    public void TheDiscount_IsZeroToOneHundred_WithUpToTwoDecimals(double? value, bool valid)
    {
        Assert.Equal(valid, CustomerDiscountRules.IsValid(value is null ? null : (decimal?)value));
    }

    // ---- POS reading --------------------------------------------------------

    [Theory]
    [InlineData("", true, null)]
    [InlineData("10", true, "10")]
    [InlineData("7,5", true, "7.5")]
    [InlineData("7.5", true, "7.5")]
    [InlineData(" 10 % ", true, "10")]
    [InlineData("12,25%", true, "12.25")]
    [InlineData("100", true, "100")]
    [InlineData("1,000", false, null)]
    [InlineData("101", false, null)]
    [InlineData("-5", false, null)]
    [InlineData("7,555", false, null)]
    [InlineData("diez", false, null)]
    [InlineData("99999999999999999999999999999999", false, null)]
    public void ThePos_ReadsADecimalCommaAsADecimal_NeverAsThousands(string text, bool ok, string? expected)
    {
        Assert.Equal(ok, CustomerFormRules.TryParseDiscount(text, out var discount));
        Assert.Equal(expected is null ? null : (decimal?)decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), discount);
    }

    [Theory]
    [InlineData(7.5, "7,5")]
    [InlineData(10.0, "10")]
    public void ThePos_ShowsTheDiscountWithADecimalComma(double value, string expected)
    {
        Assert.Equal(expected, CustomerFormRules.DiscountText((decimal)value));
    }

    // ---- wording, same in both layers ------------------------------------------

    private static string Repo(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Commerce.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    public const string DiscountHint = "De 0 a 100, sin el signo %. Admite decimales: 7,5.";
    public const string LocationLabel = "Referencias de ubicación";
    public const string LocationExample = "Ej.: Casa gris con 2 árboles al frente";
    public const string DeliveryNotesLabel = "Notas de entrega";
    public const string DeliveryNotesExample = "Ej.: Si no hay nadie, dejar con el vecino";

    [Fact]
    public void TheWebAndThePos_ExplainTheDiscountTheLocationReferenceAndTheDeliveryNotes_WithTheSameWords()
    {
        // Two different things: how to find the place, and what to do with the delivery.
        var web = Repo("src", "Commerce.Web", "src", "i18n", "locales", "es", "customers.json");
        var pos = Repo("src", "Commerce.Pos.Windows", "CustomersView.xaml");

        foreach (var text in new[] { DiscountHint, LocationLabel, LocationExample, DeliveryNotesLabel, DeliveryNotesExample })
        {
            Assert.Contains(text, web);
            Assert.Contains(text, pos);
        }

        Assert.Matches(new Regex(@"x:Name=""LocationReferenceTextBox"""), pos);
        Assert.Matches(new Regex(@"x:Name=""DeliveryNotesTextBox"""), pos);
    }

    [Fact]
    public void ThePos_SendsTheLocationReference_OnCreateAndUpdate()
    {
        var code = Repo("src", "Commerce.Pos.Windows", "CustomersView.xaml.cs");
        Assert.Matches(new Regex(@"LocationReferenceTextBox\.Text = selected\.LocationReference"), code);
        Assert.Equal(2, Regex.Matches(code, @"LocationReference: form\.LocationReference").Count);
    }
}
