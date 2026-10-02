using System.Globalization;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// purchases-receptions-and-stock T5, POS side. Pure display logic (no WPF, no I/O): the known stock of a presentation
/// with its "as of" time, and the NON-BLOCKING warning when a sale line would exceed it. A stale or missing replica
/// never blocks anything.
/// </summary>
public sealed class StockAvailabilityTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 2, 18, 30, 0, TimeSpan.Zero);

    private static string Local(DateTimeOffset at) => at.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    private static CatalogPriceReplicaItem Item(string behavior = "Weighted", string name = "Media res") => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, "Kg", "2000001", behavior, Guid.NewGuid(),
        5000m, new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

    private static ScannedSaleLineViewModel Line(Guid presentation, decimal quantity, string name = "Media res") =>
        new(presentation, null, name, "Kg", quantity, 5000m, quantity * 5000m);

    [Fact]
    public void AsOfText_IsTheLocalDayAndTime()
    {
        Assert.Equal(Local(AsOf), StockAvailability.AsOfText(AsOf));
    }

    [Theory]
    [InlineData("Weighted", "kg")]
    [InlineData("FixedQuantity", "")]
    public void QuantityText_CarriesTheKiloUnitOnlyForWeightedPresentations(string behavior, string unit)
    {
        var text = StockAvailability.QuantityText(117.5m, behavior);

        Assert.Equal((117.5m.ToString("0.###", CultureInfo.CurrentCulture) + " " + unit).Trim(), text);
    }

    [Fact]
    public void Warning_IsNullWhileTheSaleStaysWithinTheKnownStock_AndAtTheLimit()
    {
        var snapshot = new StockSnapshot(10m, AsOf);

        Assert.Null(StockAvailability.Warning(snapshot, 4m, "Weighted", "Media res"));
        Assert.Null(StockAvailability.Warning(snapshot, 10m, "Weighted", "Media res"));
    }

    [Fact]
    public void Warning_WhenTheSaleExceedsTheKnownStock_NamesTheStockAndTheTime_AndSaysItDoesNotBlock()
    {
        var warning = StockAvailability.Warning(new StockSnapshot(2.5m, AsOf), 3m, "Weighted", "Media res");

        Assert.NotNull(warning);
        Assert.Contains("Media res", warning);
        Assert.Contains(StockAvailability.QuantityText(2.5m, "Weighted"), warning);
        Assert.Contains(Local(AsOf), warning);
        Assert.Contains("no se bloquea", warning);
    }

    [Fact]
    public void Warning_WithNoKnownStock_IsNull_BecauseUnknownIsNotZero()
    {
        Assert.Null(StockAvailability.Warning(null, 99m, "Weighted", "Media res"));
    }

    [Fact]
    public void Warning_WithNegativeKnownStock_FiresForAnyPositiveSale()
    {
        Assert.NotNull(StockAvailability.Warning(new StockSnapshot(-1m, AsOf), 0.5m, "Weighted", "Media res"));
    }

    [Fact]
    public void CartWarnings_ListsOnlyTheLinesOverTheirKnownStock()
    {
        var (meat, sausage, unknown) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var known = new Dictionary<Guid, StockSnapshot>
        {
            [meat] = new(1m, AsOf), [sausage] = new(100m, AsOf),
        };

        var text = StockAvailability.CartWarnings(
            [Line(meat, 2m, "Media res"), Line(sausage, 6m, "Chorizo"), Line(unknown, 50m, "Sin dato")],
            id => known.TryGetValue(id, out var s) ? s : null);

        Assert.NotNull(text);
        Assert.Contains("Media res", text);
        Assert.DoesNotContain("Chorizo", text);
        Assert.DoesNotContain("Sin dato", text);
    }

    [Fact]
    public void CartWarnings_WhenNothingExceeds_IsNull()
    {
        var meat = Guid.NewGuid();

        Assert.Null(StockAvailability.CartWarnings([Line(meat, 1m)], _ => new StockSnapshot(5m, AsOf)));
    }

    [Fact]
    public void ProductCard_WithoutStock_ShowsNothing()
    {
        var card = new ProductCardViewModel(Item());

        Assert.False(card.HasStock);
        Assert.False(card.HasStockWarning);
    }

    [Fact]
    public void ProductCard_ShowsTheKnownStockAndItsTime()
    {
        var card = new ProductCardViewModel(Item());

        card.ApplyStock(new StockSnapshot(117.5m, AsOf));

        Assert.True(card.HasStock);
        Assert.Contains(StockAvailability.QuantityText(117.5m, "Weighted"), card.StockText);
        Assert.Contains(Local(AsOf), card.StockAsOfText);
        Assert.False(card.HasStockWarning);
    }

    [Fact]
    public void ProductCard_WarnsWhenTheLineInTheSaleExceedsTheStock_AndNotifiesTheBindings()
    {
        var item = Item();
        var card = new ProductCardViewModel(item);
        card.ApplyStock(new StockSnapshot(2m, AsOf));
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.ApplyLine(Line(item.PresentationId, 3m));

        Assert.True(card.HasStockWarning);
        Assert.Contains("no se bloquea", card.StockWarningText);
        Assert.Contains(nameof(ProductCardViewModel.HasStockWarning), raised);
        Assert.True(card.IsInSale); // the sale line is kept: a warning never blocks
    }

    [Fact]
    public void ProductCard_ClearsTheWarningWhenTheLineLeavesTheSale()
    {
        var item = Item();
        var card = new ProductCardViewModel(item);
        card.ApplyStock(new StockSnapshot(2m, AsOf));
        card.ApplyLine(Line(item.PresentationId, 3m));

        card.ApplyLine(null);

        Assert.False(card.HasStockWarning);
    }
}
