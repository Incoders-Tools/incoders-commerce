using System.Globalization;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// operator-ux-adjustments T5 and T6 on the POS: every kilos quantity is shown with the organization's decimal separator
/// (as last synced), whatever the terminal culture; typing keeps accepting a comma or a point; never synced = the
/// terminal culture, as before. Above <see cref="SaleQuantity.ConfirmAboveKilos"/> the operator confirms the kilos.
/// </summary>
public sealed class QuantityFormatTests
{
    private static readonly QuantityFormat Comma = QuantityFormat.FromOrganization("Comma");
    private static readonly QuantityFormat Dot = QuantityFormat.FromOrganization("Dot");

    private static T InCulture<T>(string culture, Func<T> read)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- T5: format ----------------------------------------------------------

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-AR")]
    public void TheOrganizationSeparator_WinsOverTheTerminalCulture(string culture)
    {
        Assert.Equal("0,550 kg", InCulture(culture, () => SaleQuantity.Text(0.55m, "Weighted", Comma)));
        Assert.Equal("0.550 kg", InCulture(culture, () => SaleQuantity.Text(0.55m, "Weighted", Dot)));
        Assert.Equal("1,250", InCulture(culture, () => SaleQuantity.EditText(1.25m, "Bulk", Comma)));
        Assert.Equal("1.250", InCulture(culture, () => SaleQuantity.EditText(1.25m, "Bulk", Dot)));
    }

    [Fact]
    public void NoThousandsSeparator_IsEverShown()
    {
        Assert.Equal("1234,500 kg", InCulture("es-AR", () => SaleQuantity.Text(1234.5m, "Weighted", Comma)));
        Assert.Equal("1234.500 kg", InCulture("es-AR", () => SaleQuantity.Text(1234.5m, "Weighted", Dot)));
        Assert.Equal("1234", InCulture("en-US", () => SaleQuantity.Text(1234m, "FixedQuantity", Comma)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Semicolon")]
    public void NeverSynced_OrUnknown_KeepsTheTerminalCulture(string? stored)
    {
        var format = QuantityFormat.FromOrganization(stored);

        Assert.Same(QuantityFormat.Terminal, format);
        Assert.Equal("0,550 kg", InCulture("es-AR", () => SaleQuantity.Text(0.55m, "Weighted", format)));
        Assert.Equal("0.550 kg", InCulture("en-US", () => SaleQuantity.Text(0.55m, "Weighted", format)));
    }

    [Theory]
    [InlineData("Comma")]
    [InlineData("Dot")]
    [InlineData(null)]
    public void TheEditText_ParsesBack_AndTypingAcceptsEitherSeparator(string? stored)
    {
        var format = QuantityFormat.FromOrganization(stored);
        var text = InCulture("en-US", () => SaleQuantity.EditText(0.55m, "Weighted", format));

        Assert.True(SaleQuantity.TryParse(text, "Weighted", out var parsed, out _));
        Assert.Equal(0.55m, parsed);
        Assert.True(SaleQuantity.TryParse("0,550", "Weighted", out var comma, out _));
        Assert.True(SaleQuantity.TryParse("0.550", "Weighted", out var dot, out _));
        Assert.Equal((0.55m, 0.55m), (comma, dot));
    }

    [Fact]
    public void CartLines_ProductCardsAndStock_ShowKilosWithTheOrganizationSeparator()
    {
        var line = new ScannedSaleLineViewModel(Guid.NewGuid(), "c", "Lengua", "Por kg", 0.55m, 1000m, 550m, QuantityBehavior: "Weighted", QuantityFormat: Dot);
        var item = new CatalogPriceReplicaItem(
            line.PresentationId, Guid.NewGuid(), Guid.NewGuid(), "Lengua", "Por kg", "c", "Weighted", Guid.NewGuid(), 1000m, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);
        var card = new ProductCardViewModel(item, Dot);
        card.ApplyLine(line);

        Assert.Equal("0.550 kg", InCulture("es-AR", () => line.QuantityText));
        Assert.Equal("0.550", InCulture("es-AR", () => line.QuantityEditText));
        Assert.Equal("0.550 kg", InCulture("es-AR", () => card.QuantityText));
        Assert.Equal("12.5 kg", InCulture("es-AR", () => StockAvailability.QuantityText(12.5m, "Weighted", Dot)));
        Assert.Equal("12,5 kg", InCulture("en-US", () => StockAvailability.QuantityText(12.5m, "Weighted", Comma)));
    }

    private sealed class FakePrices : IEffectivePriceSource
    {
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) => Task.FromResult<decimal?>(1000m);
    }

    [Fact]
    public async Task TheCart_StampsItsFormatOnEveryLine_AndFollowsAChange()
    {
        var cart = new SaleCart(new PricingResolutionService(new FakePrices())) { QuantityFormat = Comma };
        var item = new CatalogPriceReplicaItem(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Lengua", "Por kg", "c", "Weighted", Guid.NewGuid(), null, null, DateTimeOffset.UtcNow);

        await cart.AddAsync(item, 0.55m);
        Assert.Equal("0,550 kg", InCulture("en-US", () => cart.Lines.Single().QuantityText));

        cart.QuantityFormat = Dot;
        Assert.Equal("0.550 kg", InCulture("es-AR", () => cart.Lines.Single().QuantityText));
        Assert.Equal(550m, cart.Total);
    }

    // ---- T6: preventive confirmation --------------------------------------------

    [Theory]
    [InlineData(0.55, false)]
    [InlineData(50, false)]
    [InlineData(50.001, true)]
    [InlineData(550, true)]
    public void AboveFiftyKilos_TheOperatorConfirms(double kilos, bool asks)
    {
        Assert.Equal(50m, SaleQuantity.ConfirmAboveKilos);
        Assert.Equal(asks, SaleQuantity.NeedsConfirmation((decimal)kilos));
    }

    [Fact]
    public void TheQuestion_ShowsTheKilosWithTheOrganizationSeparator_AndThreeDecimals()
    {
        Assert.Equal("¿Confirmás 550,000 kg de Lengua?", InCulture("en-US", () => SaleQuantity.ConfirmationQuestion(550m, "Weighted", "Lengua", Comma)));
        Assert.Equal("¿Confirmás 50.001 kg de Lengua?", InCulture("es-AR", () => SaleQuantity.ConfirmationQuestion(50.001m, "Weighted", "Lengua", Dot)));
        Assert.Equal("¿Confirmás 60,000 de Aceite suelto?", SaleQuantity.ConfirmationQuestion(60m, "Bulk", "Aceite suelto", Comma));
    }

    // ---- T7: the confirmation looks at the kilos the LINE ends with ----------------

    [Theory]
    [InlineData(null, 30, 30)] // adding a product that is not in the sale yet
    [InlineData(30.0, 30, 60)] // adding the same weighted product merges into its line
    [InlineData(null, 60, 60)] // editing a line: the edited value replaces its kilos
    public void TheResultingKilos_AreTheLineKilosPlusTheEntered_WhenTheProductMerges(double? onLine, double entered, double resulting) =>
        Assert.Equal((decimal)resulting, SaleQuantity.ResultingQuantity((decimal?)onLine, (decimal)entered));

    [Theory]
    [InlineData(null, 30, false)]
    [InlineData(30.0, 30, true)] // 30 + 30 kg reaches 60 kg: asks, though each entry is below the threshold
    [InlineData(20.0, 30, false)] // 50 kg on the line is accepted as is
    [InlineData(20.0, 30.001, true)] // 50,001 kg on the line asks
    [InlineData(null, 50, false)] // editing to 50 kg
    [InlineData(null, 50.001, true)] // editing to 50,001 kg
    public void TheConfirmation_IsDecidedOnTheResultingLineKilos(double? onLine, double entered, bool asks) =>
        Assert.Equal(asks, SaleQuantity.NeedsConfirmation(SaleQuantity.ResultingQuantity((decimal?)onLine, (decimal)entered)));

    [Fact]
    public void TheQuestion_ShowsTheResultingTotal_AndWhatItAddsUpFrom_WhenItMerges()
    {
        Assert.Equal("¿Confirmás 60,000 kg de Lengua (30,000 + 30,000)?",
            InCulture("en-US", () => SaleQuantity.ConfirmationQuestion(30m, 30m, "Weighted", "Lengua", Comma)));
        Assert.Equal("¿Confirmás 50.001 kg de Lengua (20.000 + 30.001)?",
            InCulture("es-AR", () => SaleQuantity.ConfirmationQuestion(30.001m, 20m, "Weighted", "Lengua", Dot)));
        // Nothing on the line (a new product or an edit): just the total, as before.
        Assert.Equal("¿Confirmás 60,000 kg de Lengua?", SaleQuantity.ConfirmationQuestion(60m, null, "Weighted", "Lengua", Comma));
        Assert.Equal("¿Confirmás 60,000 de Aceite suelto (10,000 + 50,000)?",
            SaleQuantity.ConfirmationQuestion(50m, 10m, "Bulk", "Aceite suelto", Comma));
    }
}
