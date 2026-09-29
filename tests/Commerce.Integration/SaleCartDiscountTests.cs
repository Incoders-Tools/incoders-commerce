using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Percentage Discounts on Lines and on the Whole Sale",
/// on the pure cart: line and sale discounts, totals, rounding, refusal of bad
/// percentages, and that removing a discount needs no authorization while
/// adding/changing one demands an authorization value.
/// </summary>
public sealed class SaleCartDiscountTests
{
    private static readonly Guid Flour = Guid.NewGuid();
    private static readonly Guid Wine = Guid.NewGuid();
    private static readonly Guid Operator = Guid.NewGuid();
    private static readonly DiscountAuthorization Auth = new(DiscountAuthorization.BranchPin, Operator, 3);

    private sealed class FakePrices : IEffectivePriceSource
    {
        private readonly Dictionary<Guid, decimal> _prices = new();
        public FakePrices Set(Guid presentationId, decimal price) { _prices[presentationId] = price; return this; }
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
            Task.FromResult<decimal?>(_prices.TryGetValue(presentationId, out var p) ? p : null);
    }

    private static CatalogPriceReplicaItem Item(Guid presentationId, string name) => new(
        presentationId, Guid.NewGuid(), Guid.NewGuid(), name, "1u", "code-" + name, "Discrete", Guid.NewGuid(),
        null, null, DateTimeOffset.UtcNow);

    private static async Task<SaleCart> CartWithAsync(params (Guid Id, decimal Price, int Quantity)[] lines)
    {
        var prices = new FakePrices();
        foreach (var l in lines) prices.Set(l.Id, l.Price);
        var cart = new SaleCart(new PricingResolutionService(prices));
        foreach (var l in lines)
        {
            for (var i = 0; i < l.Quantity; i++) await cart.AddAsync(Item(l.Id, l.Id.ToString("N")[..4]));
        }
        return cart;
    }

    [Fact]
    public async Task LineDiscount_ReducesOnlyThatLine_AndTheTotal()
    {
        var cart = await CartWithAsync((Flour, 1000m, 1), (Wine, 500m, 1));

        var result = cart.SetLineDiscount(Flour, 10m, Auth);

        Assert.True(result.Succeeded);
        var line = cart.Lines.Single(l => l.PresentationId == Flour);
        Assert.Equal(10m, line.LineDiscountPercent);
        Assert.Equal(100m, line.LineDiscountAmount);
        Assert.Equal(1000m, line.LineTotal); // still the undiscounted amount
        Assert.Equal(900m, line.NetTotal);
        Assert.Null(cart.Lines.Single(l => l.PresentationId == Wine).LineDiscountPercent);
        Assert.Equal(1500m, cart.Subtotal);
        Assert.Equal(1400m, cart.Total);
        Assert.Equal(100m, cart.DiscountTotal);
    }

    [Fact]
    public async Task SaleDiscount_AppliesToTheSubtotalAfterLineDiscounts()
    {
        var cart = await CartWithAsync((Flour, 1000m, 1)); // one line of 1000
        cart.SetLineDiscount(Flour, 10m, Auth);            // 1000 -> 900

        var result = cart.SetSaleDiscount(5m, Auth);

        Assert.True(result.Succeeded);
        Assert.Equal(900m, cart.NetSubtotal);
        Assert.Equal(45m, cart.SaleDiscountAmount);
        Assert.Equal(855m, cart.Total);
        Assert.Equal(145m, cart.DiscountTotal);
    }

    [Fact]
    public async Task DiscountAmounts_RoundHalfAwayFromZero()
    {
        var cart = await CartWithAsync((Flour, 10.05m, 1));

        cart.SetLineDiscount(Flour, 10m, Auth);

        var line = cart.Lines.Single();
        Assert.Equal(1.01m, line.LineDiscountAmount);
        Assert.Equal(9.04m, line.NetTotal);
        Assert.Equal(9.04m, cart.Total);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("100.5")]
    [InlineData("12.345")]
    public async Task InvalidPercent_IsRefused_AndChangesNothing(string percent)
    {
        var cart = await CartWithAsync((Flour, 100m, 1));
        var value = decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture);

        var line = cart.SetLineDiscount(Flour, value, Auth);
        var sale = cart.SetSaleDiscount(value, Auth);

        Assert.False(line.Succeeded);
        Assert.False(sale.Succeeded);
        Assert.NotNull(line.Message);
        Assert.Equal(100m, cart.Total);
        Assert.Null(cart.Lines.Single().LineDiscountPercent);
        Assert.Null(cart.SaleDiscountPercent);
        Assert.Null(cart.Authorization);
    }

    [Fact]
    public async Task FullDiscount_NeverMakesTheTotalNegative()
    {
        var cart = await CartWithAsync((Flour, 100m, 1));

        cart.SetLineDiscount(Flour, 100m, Auth);
        cart.SetSaleDiscount(100m, Auth);

        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public async Task UnknownLine_IsRefused()
    {
        var cart = await CartWithAsync((Flour, 100m, 1));

        var result = cart.SetLineDiscount(Guid.NewGuid(), 10m, Auth);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ChangingADiscount_ReplacesItAndRecordsTheLatestAuthorization()
    {
        var cart = await CartWithAsync((Flour, 1000m, 1));
        cart.SetLineDiscount(Flour, 10m, Auth);
        var second = new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 4);

        cart.SetLineDiscount(Flour, 20m, second);

        Assert.Equal(200m, cart.Lines.Single().LineDiscountAmount);
        Assert.Equal(second, cart.Authorization);
    }

    [Fact]
    public async Task RemovingDiscounts_NeedsNoAuthorization_AndClearsTheMarkerWhenNoneRemain()
    {
        var cart = await CartWithAsync((Flour, 1000m, 1), (Wine, 500m, 1));
        cart.SetLineDiscount(Flour, 10m, Auth);
        cart.SetSaleDiscount(5m, Auth);

        Assert.True(cart.RemoveLineDiscount(Flour));
        Assert.NotNull(cart.Authorization); // the sale discount still stands
        Assert.True(cart.RemoveSaleDiscount());

        Assert.Equal(1500m, cart.Total);
        Assert.Null(cart.Authorization);
        Assert.False(cart.HasDiscount);
        Assert.False(cart.RemoveSaleDiscount());
    }

    [Fact]
    public async Task ChangingQuantity_KeepsThePercentAndRecomputesTheAmounts()
    {
        var cart = await CartWithAsync((Flour, 100m, 1));
        cart.SetLineDiscount(Flour, 10m, Auth);
        cart.SetSaleDiscount(10m, Auth);

        await cart.SetQuantityAsync(Flour, 3m);

        var line = cart.Lines.Single();
        Assert.Equal(10m, line.LineDiscountPercent);
        Assert.Equal(30m, line.LineDiscountAmount);
        Assert.Equal(270m, cart.NetSubtotal);
        Assert.Equal(27m, cart.SaleDiscountAmount);
        Assert.Equal(243m, cart.Total);
    }

    [Fact]
    public async Task RemovingTheLineOrClearing_DropsItsDiscountAndTheSaleDiscountWhenEmpty()
    {
        var cart = await CartWithAsync((Flour, 100m, 1), (Wine, 100m, 1));
        cart.SetLineDiscount(Flour, 10m, Auth);
        cart.SetSaleDiscount(10m, Auth);

        cart.Remove(Flour);
        Assert.Equal(100m, cart.NetSubtotal);
        Assert.Equal(10m, cart.SaleDiscountAmount);

        cart.Clear();
        Assert.Null(cart.SaleDiscountPercent);
        Assert.Null(cart.Authorization);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public async Task SaleDiscount_OnAnEmptySale_IsRefused()
    {
        var cart = new SaleCart(new PricingResolutionService(new FakePrices()));

        Assert.False(cart.SetSaleDiscount(5m, Auth).Succeeded);
    }
}
