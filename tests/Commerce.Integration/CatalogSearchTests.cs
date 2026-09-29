using Commerce.BranchNode;

namespace Commerce.Integration;

/// <summary>
/// Local name search over the `catalog_replica` (desktop POS redesign T3):
/// case- and accent-insensitive on product/presentation name, prefix match on
/// the identification code, organization scoped, capped.
/// </summary>
public sealed class CatalogSearchTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-catalog-search-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private CatalogPriceReplicaItem Item(string product, string presentation, string? code, Guid? organizationId = null) => new(
        PresentationId: Guid.NewGuid(),
        OrganizationId: organizationId ?? _organizationId,
        ProductId: Guid.NewGuid(),
        ProductName: product,
        PresentationName: presentation,
        IdentificationCode: code,
        QuantityBehavior: "Integral",
        UnitId: Guid.NewGuid(),
        UnitPrice: 10m,
        EffectiveFrom: DateOnly.FromDateTime(DateTime.UtcNow),
        UpdatedAtUtc: DateTimeOffset.UtcNow);

    private BranchSyncStore Seed(params CatalogPriceReplicaItem[] items)
    {
        var store = new BranchSyncStore(ConnectionString);
        store.ApplyCatalogPriceSync(items, [], DateTimeOffset.UtcNow);
        return store;
    }

    [Fact]
    public void SearchCatalog_MatchesProductName_IgnoringCaseAndAccents()
    {
        using var store = Seed(
            Item("Café Molido", "250g", "111"),
            Item("Harina", "1kg", "222"));

        var result = store.SearchCatalog(_organizationId, "CAFE");

        Assert.Equal("Café Molido", Assert.Single(result.Items).ProductName);
    }

    [Fact]
    public void SearchCatalog_TypedAccentMatchesUnaccentedName()
    {
        using var store = Seed(Item("Cafe Tostado", "1kg", "111"));

        Assert.Single(store.SearchCatalog(_organizationId, "café").Items);
    }

    [Fact]
    public void SearchCatalog_MatchesPresentationName()
    {
        using var store = Seed(
            Item("Harina", "Bolsa 1kg", "111"),
            Item("Harina", "Bolsa 25kg", "112"));

        var result = store.SearchCatalog(_organizationId, "25kg");

        Assert.Equal("Bolsa 25kg", Assert.Single(result.Items).PresentationName);
    }

    [Fact]
    public void SearchCatalog_MatchesIdentificationCodePrefix_ButNotInfix()
    {
        using var store = Seed(
            Item("Yerba", "1kg", "7791234"),
            Item("Azucar", "1kg", "8887791"));

        var result = store.SearchCatalog(_organizationId, "77912");

        Assert.Equal("Yerba", Assert.Single(result.Items).ProductName);
        Assert.Empty(store.SearchCatalog(_organizationId, "7791").Items.Where(i => i.ProductName == "Azucar"));
    }

    [Fact]
    public void SearchCatalog_MultipleWords_MustAllMatch()
    {
        using var store = Seed(
            Item("Harina de trigo", "1kg", "1"),
            Item("Harina de maiz", "1kg", "2"));

        var result = store.SearchCatalog(_organizationId, "harina maiz");

        Assert.Equal("Harina de maiz", Assert.Single(result.Items).ProductName);
    }

    [Fact]
    public void SearchCatalog_TreatsLikeWildcardsLiterally()
    {
        using var store = Seed(Item("Aceite 100%", "1l", "1"), Item("Aceite", "2l", "2"));

        var result = store.SearchCatalog(_organizationId, "%");

        Assert.Equal("Aceite 100%", Assert.Single(result.Items).ProductName);
    }

    [Fact]
    public void SearchCatalog_IsScopedToTheOrganization()
    {
        using var store = Seed(
            Item("Harina", "1kg", "1"),
            Item("Harina ajena", "1kg", "2", organizationId: Guid.NewGuid()));

        Assert.Equal("Harina", Assert.Single(store.SearchCatalog(_organizationId, "harina").Items).ProductName);
    }

    [Fact]
    public void SearchCatalog_EmptyQuery_ReturnsAllOrderedByName_AndCapsWithTruncatedFlag()
    {
        using var store = Seed(
            Item("Cafe", "b", "3"), Item("Azucar", "a", "1"), Item("Cafe", "a", "2"));

        var all = store.SearchCatalog(_organizationId, "  ");
        Assert.False(all.Truncated);
        Assert.Equal(["Azucar", "Cafe", "Cafe"], all.Items.Select(i => i.ProductName));
        Assert.Equal(["a", "a", "b"], all.Items.Select(i => i.PresentationName));

        var capped = store.SearchCatalog(_organizationId, null, limit: 2);
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Items.Count);
    }

    [Fact]
    public void SearchCatalog_IncludesPriceWhenPresent()
    {
        using var store = Seed(Item("Harina", "1kg", "1"));

        Assert.Equal(10m, store.SearchCatalog(_organizationId, "harina").Items[0].UnitPrice);
    }
}
