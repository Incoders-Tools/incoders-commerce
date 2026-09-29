using System.ComponentModel;
using System.Globalization;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>Pure display logic behind the catalog cards; no WPF, no I/O.</summary>
public sealed class ProductCardViewModelTests
{
    private static CatalogPriceReplicaItem Item(string product, string presentation, decimal? price) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), product, presentation, "2000001", "Discrete", Guid.NewGuid(),
        price, price is null ? null : new DateOnly(2026, 1, 1), DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("Carbón Vegetal", "CV")]
    [InlineData("vino tinto malbec", "VT")]
    [InlineData("Aceite", "A")]
    [InlineData("  ", "?")]
    public void Initials_UseTheFirstLettersOfTheFirstTwoWords(string name, string expected)
    {
        var card = new ProductCardViewModel(Item(name, "1kg", 10m));

        Assert.Equal(expected, card.Initials);
    }

    [Fact]
    public void NotInTheSale_ShowsCatalogPrice_QuantityZero_AndZeroTotal()
    {
        var card = new ProductCardViewModel(Item("Flour", "1kg", 100m));

        Assert.Equal(0m, card.Quantity);
        Assert.Equal(100m.ToString("C", CultureInfo.CurrentCulture), card.UnitPriceText);
        Assert.Equal(0m.ToString("C", CultureInfo.CurrentCulture), card.LineTotalText);
        Assert.True(card.HasPrice);
        Assert.False(card.IsInSale);
    }

    [Fact]
    public void WithoutAPrice_ReportsNoPrice()
    {
        var card = new ProductCardViewModel(Item("Flour", "1kg", null));

        Assert.False(card.HasPrice);
        Assert.Equal("Sin precio", card.UnitPriceText);
    }

    [Fact]
    public void ApplyLine_TakesQuantityAndResolvedPricesFromTheSale_AndNotifies()
    {
        var item = Item("Flour", "1kg", 100m);
        var card = new ProductCardViewModel(item);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.ApplyLine(new ScannedSaleLineViewModel(item.PresentationId, "2000001", "Flour", "1kg", 3m, 95m, 285m));

        Assert.Equal(3m, card.Quantity);
        Assert.True(card.IsInSale);
        Assert.Equal(95m.ToString("C", CultureInfo.CurrentCulture), card.UnitPriceText);
        Assert.Equal(285m.ToString("C", CultureInfo.CurrentCulture), card.LineTotalText);
        Assert.Contains(nameof(ProductCardViewModel.Quantity), raised);

        card.ApplyLine(null);
        Assert.Equal(0m, card.Quantity);
        Assert.False(card.IsInSale);
    }
}
