using Commerce.Domain.Pricing;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T3: the price breakdown shown to an admin (base, each component with the amount it is computed
/// on and the amount it adds, final price). It is derived by the SAME arithmetic as <see cref="RateComponentSet.Compose"/>,
/// so what is displayed is what a sale charges.
/// </summary>
public sealed class RateComponentBreakdownTests
{
    private static RateComponentSet Reparto() => RateComponentSet.ForPriceList(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1),
        [
            new("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new("IB", "Ingresos Brutos (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4),
        ]);

    [Fact]
    public void Breakdown_ListsBaseEachComponentWithItsAmount_AndTheFinalPrice()
    {
        var breakdown = Reparto().Breakdown(11_400m);

        Assert.Equal(11_400m, breakdown.BasePrice);
        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], breakdown.Lines.Select(l => l.Code));
        Assert.Equal([1_197m, 285m, 798m, 2_850m], breakdown.Lines.Select(l => l.Amount));
        Assert.All(breakdown.Lines, l => Assert.Equal(11_400m, l.CalculationAmount)); // all four apply to the base
        Assert.Equal(16_530m, breakdown.FinalPrice); // 11.400 x 1,45
    }

    [Fact]
    public void Breakdown_FinalPrice_IsExactlyWhatComposeReturns()
    {
        var set = Reparto();
        foreach (var basePrice in new[] { 10_600m, 7_817.57m, 0.01m, 25_000m })
        {
            Assert.Equal(set.Compose(basePrice), set.Breakdown(basePrice).FinalPrice);
        }
    }

    [Fact]
    public void Breakdown_ASubtotalComponent_IsComputedOnTheRunningSubtotal()
    {
        var set = RateComponentSet.ForPriceList(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1),
        [
            new("IVA", "IVA", 10m, RateCalculationBase.Base, 1),
            new("FLETE", "Flete", 10m, RateCalculationBase.Subtotal, 2),
        ]);

        var breakdown = set.Breakdown(100m);

        Assert.Equal(100m, breakdown.Lines[0].CalculationAmount);
        Assert.Equal(110m, breakdown.Lines[1].CalculationAmount); // the subtotal after IVA
        Assert.Equal(11m, breakdown.Lines[1].Amount);
        Assert.Equal(121m, breakdown.FinalPrice);
    }

    [Fact]
    public void Breakdown_OfAnEmptySet_IsJustTheBase()
    {
        var empty = RateComponentSet.ForOrganizationDefault(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), []);

        var breakdown = empty.Breakdown(123.45m);

        Assert.Empty(breakdown.Lines);
        Assert.Equal(123.45m, breakdown.FinalPrice);
    }
}
