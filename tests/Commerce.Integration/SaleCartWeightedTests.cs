using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// operator-ux-adjustments T3: a weighted presentation is sold in kilos with decimals. The line is priced by the shared
/// <see cref="PricingResolutionService"/> at the kilos (price per kilo x kilos, rounded with the one money rule), a
/// weighted product is never added as "one" without its kilos, and fixed-quantity products keep whole units.
/// </summary>
public sealed class SaleCartWeightedTests
{
    private static readonly Guid Lengua = Guid.NewGuid();
    private static readonly Guid Chorizo = Guid.NewGuid();

    private sealed class FakePrices : IEffectivePriceSource
    {
        private readonly Dictionary<Guid, decimal> _prices = new();
        public FakePrices Set(Guid presentationId, decimal price) { _prices[presentationId] = price; return this; }
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
            Task.FromResult<decimal?>(_prices.TryGetValue(presentationId, out var p) ? p : null);
    }

    private static CatalogPriceReplicaItem Item(Guid presentationId, string product, string presentation, string behavior) => new(
        presentationId, Guid.NewGuid(), Guid.NewGuid(), product, presentation, "code-" + product, behavior, Guid.NewGuid(),
        null, null, DateTimeOffset.UtcNow);

    private static CatalogPriceReplicaItem LenguaPorKg => Item(Lengua, "Lengua", "Por kg", "Weighted");

    private static CatalogPriceReplicaItem ChorizoUnidad => Item(Chorizo, "Chorizo", "Unidad", "FixedQuantity");

    private static SaleCart NewCart(FakePrices prices) => new(new PricingResolutionService(prices));

    [Fact]
    public async Task AddingAWeightedProduct_WithItsKilos_PricesPricePerKiloTimesKilos()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 12_345.67m));

        var result = await cart.AddAsync(LenguaPorKg, 0.550m);

        Assert.True(result.Succeeded);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(0.550m, line.Quantity);
        Assert.Equal(12_345.67m, line.UnitPrice);
        Assert.Equal(6_790.12m, line.LineTotal); // 12.345,67 x 0,550 = 6.790,1185
        Assert.Equal("Weighted", line.QuantityBehavior);
        Assert.Equal(6_790.12m, cart.Total);
    }

    [Fact]
    public async Task TheWeightedLineTotal_UsesTheSharedMoneyRounding_HalfAwayFromZero()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 10.01m));

        await cart.AddAsync(LenguaPorKg, 0.5m);

        // 10,01 x 0,5 = 5,005: half away from zero gives 5,01 (banker's rounding would give 5,00).
        Assert.Equal(5.01m, cart.Lines.Single().LineTotal);
    }

    [Fact]
    public async Task AddingAWeightedProduct_WithoutKilos_IsRefused_AndAddsNothing()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));

        var result = await cart.AddAsync(LenguaPorKg);

        Assert.False(result.Succeeded);
        Assert.Contains("kilos", result.Message);
        Assert.Empty(cart.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(0.5555)]
    public async Task AddingAWeightedProduct_WithInvalidKilos_IsRefused(double kilos)
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));

        var result = await cart.AddAsync(LenguaPorKg, (decimal)kilos);

        Assert.False(result.Succeeded);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task AddingTheSameWeightedProductAgain_AddsTheNewKilosToTheLine()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));

        await cart.AddAsync(LenguaPorKg, 0.550m);
        await cart.AddAsync(LenguaPorKg, 0.300m);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(0.850m, line.Quantity);
        Assert.Equal(850m, line.LineTotal);
    }

    [Fact]
    public async Task EditingAWeightedLine_SetsTheKilos_AndRefusesZeroNegativeOrFourDecimals()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));
        await cart.AddAsync(LenguaPorKg, 0.550m);

        Assert.True((await cart.SetQuantityAsync(Lengua, 1.234m)).Succeeded);
        Assert.Equal(1.234m, cart.Lines.Single().Quantity);
        Assert.Equal(1_234m, cart.Total);

        Assert.False((await cart.SetQuantityAsync(Lengua, 0m)).Succeeded);
        Assert.False((await cart.SetQuantityAsync(Lengua, -1m)).Succeeded);
        Assert.False((await cart.SetQuantityAsync(Lengua, 1.2345m)).Succeeded);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(1.234m, line.Quantity);
        Assert.Equal(1_234m, cart.Total);
    }

    [Fact]
    public async Task StepButtons_NeverStepAWeightedLineByOneKilo()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));
        await cart.AddAsync(LenguaPorKg, 0.550m);

        Assert.False((await cart.IncrementAsync(Lengua)).Succeeded);
        Assert.False((await cart.DecrementAsync(Lengua)).Succeeded);
        Assert.Equal(0.550m, cart.Lines.Single().Quantity);
    }

    [Fact]
    public async Task AFixedQuantityProduct_StillAddsOne_AndKeepsWholeUnits()
    {
        var cart = NewCart(new FakePrices().Set(Chorizo, 800m));

        await cart.AddAsync(ChorizoUnidad);
        await cart.AddAsync(ChorizoUnidad);
        await cart.IncrementAsync(Chorizo);
        Assert.Equal(3m, cart.Lines.Single().Quantity);

        var fractional = await cart.SetQuantityAsync(Chorizo, 2.5m);
        Assert.False(fractional.Succeeded);
        Assert.Contains("enteras", fractional.Message);
        Assert.Equal(3m, cart.Lines.Single().Quantity);

        Assert.False((await cart.AddAsync(ChorizoUnidad, 0.5m)).Succeeded);
        Assert.Equal(2_400m, cart.Total);
    }

    [Fact]
    public async Task ReplacingTheBuyer_KeepsTheKilosAndTheBehaviorOfTheLine()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));
        await cart.AddAsync(LenguaPorKg, 0.550m);

        Assert.True((await cart.SetCustomerAsync(null)).Succeeded);

        var line = cart.Lines.Single();
        Assert.Equal(0.550m, line.Quantity);
        Assert.Equal("Weighted", line.QuantityBehavior);
        Assert.Equal(550m, line.LineTotal);
    }

    [Fact]
    public async Task TheCommittedLine_CarriesTheKilosAndTheRoundedTotal()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 12_345.67m));
        await cart.AddAsync(LenguaPorKg, 0.550m);

        var line = Assert.Single(cart.BuildSaleLines(Guid.NewGuid()));

        Assert.Equal(0.550m, line.Quantity);
        Assert.Equal(6_790.12m, line.LineTotal);
    }

    [Fact]
    public async Task TheStockWarning_ReadsTheKilosOfTheLine()
    {
        var cart = NewCart(new FakePrices().Set(Lengua, 1000m));
        await cart.AddAsync(LenguaPorKg, 2.5m);

        var warning = StockAvailability.CartWarnings(cart.Lines, _ => new StockSnapshot(2.25m, DateTimeOffset.UtcNow));

        Assert.NotNull(warning);
        Assert.Contains("kg", warning);
    }
}
