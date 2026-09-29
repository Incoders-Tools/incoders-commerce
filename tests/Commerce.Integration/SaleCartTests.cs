using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The scanned-sale state extracted from <c>MainWindow</c>: pure cart
/// behavior over the shared <see cref="PricingResolutionService"/>, no WPF and
/// no SQLite. Prices come from a fake <see cref="IEffectivePriceSource"/>.
/// </summary>
public sealed class SaleCartTests
{
    private static readonly Guid Flour = Guid.NewGuid();
    private static readonly Guid Wine = Guid.NewGuid();

    private sealed class FakePrices : IEffectivePriceSource
    {
        private readonly Dictionary<Guid, decimal> _prices = new();
        public FakePrices Set(Guid presentationId, decimal price) { _prices[presentationId] = price; return this; }
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
            Task.FromResult<decimal?>(_prices.TryGetValue(presentationId, out var p) ? p : null);
    }

    private static CatalogPriceReplicaItem Item(Guid presentationId, string product, string presentation, string? code) => new(
        presentationId, Guid.NewGuid(), Guid.NewGuid(), product, presentation, code, "Discrete", Guid.NewGuid(),
        null, null, DateTimeOffset.UtcNow);

    private static SaleCart NewCart(FakePrices prices) => new(new PricingResolutionService(prices));

    [Fact]
    public async Task Add_NewPresentation_AppendsLineAtQuantityOne_WithResolvedPrice()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m));

        var result = await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        Assert.True(result.Succeeded);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal(100m, line.LineTotal);
        Assert.Equal("111", line.IdentificationCode);
        Assert.Equal(100m, cart.Total);
    }

    [Fact]
    public async Task Add_SamePresentationAgain_BumpsQuantityInsteadOfAddingALine()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m));
        var item = Item(Flour, "Flour", "1kg", "111");

        await cart.AddAsync(item);
        await cart.AddAsync(item);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(200m, line.LineTotal);
        Assert.Equal(200m, cart.Total);
    }

    [Fact]
    public async Task Add_WithoutEffectivePrice_ReportsFailure_AndAddsNothing()
    {
        var cart = NewCart(new FakePrices());

        var result = await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        Assert.False(result.Succeeded);
        Assert.Contains("No hay precio vigente", result.Message);
        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public async Task Add_WithoutEffectivePrice_LeavesExistingLineUntouched()
    {
        var prices = new FakePrices().Set(Flour, 100m);
        var cart = NewCart(prices);
        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));
        var unpriced = Item(Wine, "Wine", "750ml", "222");

        var result = await cart.AddAsync(unpriced);

        Assert.False(result.Succeeded);
        Assert.Single(cart.Lines);
        Assert.Equal(100m, cart.Total);
    }

    [Fact]
    public async Task Increment_And_Decrement_ChangeQuantityAndTotal()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m));
        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        await cart.IncrementAsync(Flour);
        await cart.IncrementAsync(Flour);
        Assert.Equal(3m, cart.Lines[0].Quantity);
        Assert.Equal(300m, cart.Total);

        await cart.DecrementAsync(Flour);
        Assert.Equal(2m, cart.Lines[0].Quantity);
        Assert.Equal(200m, cart.Total);
    }

    [Fact]
    public async Task Decrement_AtQuantityOne_RemovesTheLine()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m));
        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        await cart.DecrementAsync(Flour);

        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public async Task SetQuantity_ReResolvesPrice_AndZeroOrLessRemoves()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 12.5m));
        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        var result = await cart.SetQuantityAsync(Flour, 4m);

        Assert.True(result.Succeeded);
        Assert.Equal(4m, cart.Lines[0].Quantity);
        Assert.Equal(50m, cart.Lines[0].LineTotal);

        await cart.SetQuantityAsync(Flour, 0m);
        Assert.Empty(cart.Lines);
    }

    [Fact]
    public async Task SetQuantity_ForUnknownLine_Fails()
    {
        var cart = NewCart(new FakePrices());

        var result = await cart.SetQuantityAsync(Flour, 2m);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Remove_And_Clear_DropLines_AndTotalFollows()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m).Set(Wine, 950m));
        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));
        await cart.AddAsync(Item(Wine, "Wine", "750ml", "222"));
        Assert.Equal(1050m, cart.Total);

        Assert.True(cart.Remove(Flour));
        Assert.False(cart.Remove(Flour));
        Assert.Equal(950m, cart.Total);

        cart.Clear();
        Assert.Empty(cart.Lines);
        Assert.Equal(0m, cart.Total);
        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public async Task Changes_RaiseTotalAndIsEmptyNotifications()
    {
        var cart = NewCart(new FakePrices().Set(Flour, 100m));
        var raised = new List<string?>();
        cart.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await cart.AddAsync(Item(Flour, "Flour", "1kg", "111"));

        Assert.Contains(nameof(SaleCart.Total), raised);
        Assert.Contains(nameof(SaleCart.IsEmpty), raised);
    }
}
