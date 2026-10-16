using Commerce.BranchNode;
using Commerce.Pos.Windows;
using Commerce.Pos.Windows.Controls;

namespace Commerce.Integration;

/// <summary>
/// Sale screen polish: the customer picker search (<see cref="SaleCustomerPicker.Filter"/>), the category rail that does
/// not offer Embutidos and Achuras as filters, and the product grid that shares its whole width among the cards
/// (<see cref="UniformWrapPanel.Layout"/>). Pure logic, no WPF window.
/// </summary>
public sealed class PosSaleScreenLayoutTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Jose = Guid.NewGuid();
    private static readonly Guid Maria = Guid.NewGuid();
    private static readonly Guid Parrilla = Guid.NewGuid();

    private static IReadOnlyList<SaleCustomerPickerItem> Items() => SaleCustomerPicker.BuildItems(
    [
        new CustomerReplica(Jose, Org, "José Cabrera", "Retail", "20123456789", null, "Moreno", DateTimeOffset.UtcNow),
        new CustomerReplica(Maria, Org, "María Gómez", "Retail", null, null, null, DateTimeOffset.UtcNow),
        new CustomerReplica(Parrilla, Org, "Parrilla Don Julio", "Wholesale", "30712345678", null, "Ituzaingó", DateTimeOffset.UtcNow),
    ]);

    [Fact]
    public void BuildItems_PutsWalkInFirst_AndShowsTaxIdAndLocalityAsDetail()
    {
        var items = Items();

        Assert.Equal([SaleCustomerPicker.WalkInLabel, "José Cabrera", "María Gómez", "Parrilla Don Julio"], items.Select(i => i.Label));
        Assert.Equal("20123456789 · Moreno", items[1].Detail);
        Assert.False(items[2].HasDetail);
    }

    [Theory]
    [InlineData("jose", "José Cabrera")]
    [InlineData("CAB jo", "José Cabrera")]
    [InlineData("gomez", "María Gómez")]
    [InlineData("ituzaingo", "Parrilla Don Julio")]
    [InlineData("30-71234", "Parrilla Don Julio")]
    [InlineData("3071234", "Parrilla Don Julio")]
    public void Filter_MatchesEveryWord_IgnoringCaseAccentsAndTaxIdSeparators(string term, string expected)
    {
        var matches = SaleCustomerPicker.Filter(Items(), term);

        Assert.Equal([SaleCustomerPicker.WalkInLabel, expected], matches.Select(m => m.Label));
    }

    [Fact]
    public void Filter_WithABlankTerm_KeepsEveryRow_AndWithNoMatch_OnlyWalkIn()
    {
        Assert.Equal(4, SaleCustomerPicker.Filter(Items(), "  ").Count);
        Assert.Null(Assert.Single(SaleCustomerPicker.Filter(Items(), "zzz")).CustomerId);
    }

    [Fact]
    public void TheRail_DoesNotOfferEmbutidosNorAchuras_ButKeepsTheRest()
    {
        var items = CategoryRailItem.Build(
        [
            new CatalogCategory(Guid.NewGuid(), "Achuras", "meat"),
            new CatalogCategory(Guid.NewGuid(), "Almacén", "grocery"),
            new CatalogCategory(Guid.NewGuid(), "Bebidas", "drinks"),
            new CatalogCategory(Guid.NewGuid(), "EMBUTIDOS", "meat"),
            new CatalogCategory(Guid.NewGuid(), "Vacuno", "meat"),
        ]);

        Assert.Equal(["Todos", "Almacén", "Bebidas", "Vacuno"], items.Select(i => i.Name));
    }

    [Theory]
    [InlineData(406, 2, 198)] // 1180 px window: two cards share the column, no empty strip
    [InlineData(700, 3, 226.67)] // wider screens get one more column
    [InlineData(150, 1, 150)] // narrower than one card: one card, as wide as the panel
    public void TheCardsGrid_SharesTheWholeWidth_AmongAsManyColumnsAsFit(double width, int columns, double itemWidth)
    {
        var layout = UniformWrapPanel.Layout(width, minItemWidth: 190, spacing: 10);

        Assert.Equal(columns, layout.Columns);
        Assert.Equal(itemWidth, Math.Round(layout.ItemWidth, 2));
        Assert.Equal(width, Math.Round(layout.Columns * layout.ItemWidth + (layout.Columns - 1) * 10, 2));
    }
}
