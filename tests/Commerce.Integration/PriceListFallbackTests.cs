using Commerce.Application.Pricing;
using Commerce.Domain.Pricing;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T6: when the buyer's list has no effective price for a presentation, the organization default
/// list (Mostrador) prices it, with THAT list's composition, then the customer discount. The outcome says which list
/// priced the line. Pure Application code: the cloud and the POS run this same compiled rule (ADR-010).
/// </summary>
public sealed class PriceListFallbackTests
{
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Lengua = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 3);

    private sealed class FakePrices(Dictionary<Guid, decimal> prices) : IEffectivePriceSource
    {
        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct) =>
            Task.FromResult(prices.TryGetValue(presentationId, out var p) ? p : (decimal?)null);
    }

    private sealed class FakeRates(RateComponentSet? set) : IEffectiveRateComponentSource
    {
        public Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct) => Task.FromResult(set);
    }

    private static RateComponentSet Markup(decimal percent) => RateComponentSet.ForPriceList(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today, [new RateComponent("REMARCACION", "Remarcación", percent, RateCalculationBase.Base, 1)]);

    private static PricingResolutionService Service(Dictionary<Guid, decimal> reparto, Dictionary<Guid, decimal> mostrador) =>
        new(new PriceListPorts(Reparto, new FakePrices(reparto), new FakeRates(Markup(25m))),
            new PriceListPorts(Mostrador, new FakePrices(mostrador), new FakeRates(Markup(35m))));

    [Fact]
    public async Task WhenTheBuyersListPricesIt_ItIsUsed_AndNotMarkedAsFallback()
    {
        var service = Service(new() { [Lengua] = 10_000m }, new() { [Lengua] = 8_565m });

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(
            await service.ResolveAsync(Lengua, 1m, 0m, Today, CancellationToken.None));

        Assert.Equal(12_500m, resolved.UnitListPrice);
        Assert.Equal(Reparto, resolved.PricedFromListId);
        Assert.False(resolved.FellBack);
    }

    [Fact]
    public async Task WhenTheBuyersListHasNoPrice_TheDefaultListPricesIt_WithItsOwnCompositionThenTheDiscount()
    {
        var service = Service([], new() { [Lengua] = 8_565m });

        var resolved = Assert.IsType<PriceResolutionOutcome.Resolved>(
            await service.ResolveAsync(Lengua, 2m, 10m, Today, CancellationToken.None));

        Assert.Equal(11_562.75m, resolved.UnitListPrice); // 8.565 x 1,35: Mostrador's composition, not Reparto's 1,25
        Assert.Equal(10_406.48m, resolved.UnitNetPrice);  // less 10 %
        Assert.Equal(20_812.96m, resolved.LineTotal);
        Assert.Equal(Mostrador, resolved.PricedFromListId);
        Assert.True(resolved.FellBack);
    }

    [Fact]
    public async Task WhenNeitherListHasAPrice_TheOutcomeIsStillNoEffectivePrice()
    {
        var service = Service([], []);

        var outcome = await service.ResolveAsync(Lengua, 1m, 0m, Today, CancellationToken.None);

        Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(outcome);
    }

    [Fact]
    public async Task ABuyerPricedFromTheDefaultListItself_NeverFallsBackToIt()
    {
        var service = new PricingResolutionService(new PriceListPorts(Mostrador, new FakePrices([]), new FakeRates(null)), fallback: null);

        Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(await service.ResolveAsync(Lengua, 1m, 0m, Today, CancellationToken.None));
    }
}
