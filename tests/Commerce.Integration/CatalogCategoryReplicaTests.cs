using Commerce.BranchNode;
using Commerce.Pos.Windows;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// catalog-categories T4c: the POS replica keeps each presentation's category
/// (id, name, icon key), migrates an existing `branch.db` idempotently, lists
/// the distinct local categories for the rail, and filters the cards by one.
/// </summary>
public sealed class CatalogCategoryReplicaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-category-replica-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();

    private static readonly Guid Meat = Guid.NewGuid();
    private static readonly Guid Wine = Guid.NewGuid();

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private CatalogPriceReplicaItem Item(
        string product, string? code, Guid? categoryId = null, string? categoryName = null, string? iconKey = null,
        Guid? organizationId = null, Guid? presentationId = null) => new(
        PresentationId: presentationId ?? Guid.NewGuid(),
        OrganizationId: organizationId ?? _organizationId,
        ProductId: Guid.NewGuid(),
        ProductName: product,
        PresentationName: "1kg",
        IdentificationCode: code,
        QuantityBehavior: "Integral",
        UnitId: Guid.NewGuid(),
        UnitPrice: 10m,
        EffectiveFrom: DateOnly.FromDateTime(DateTime.UtcNow),
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        CategoryId: categoryId,
        CategoryName: categoryName,
        CategoryIconKey: iconKey);

    private BranchSyncStore Seed(params CatalogPriceReplicaItem[] items)
    {
        var store = new BranchSyncStore(ConnectionString);
        store.ApplyCatalogPriceSync(items, [], DateTimeOffset.UtcNow);
        return store;
    }

    [Fact]
    public void ApplyCatalogPriceSync_StoresTheCategoryOfEachRow_AndReplacesItOnRename()
    {
        var presentationId = Guid.NewGuid();
        using var store = Seed(Item("Bife", "111", Meat, "Carnes", "meat", presentationId: presentationId));

        var stored = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Equal(Meat, stored.CategoryId);
        Assert.Equal("Carnes", stored.CategoryName);
        Assert.Equal("meat", stored.CategoryIconKey);

        store.ApplyCatalogPriceSync(
            [Item("Bife", "111", Meat, "Carnes rojas", "charcoal", presentationId: presentationId)], [], DateTimeOffset.UtcNow);

        var renamed = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Equal("Carnes rojas", renamed.CategoryName);
        Assert.Equal("charcoal", renamed.CategoryIconKey);
    }

    [Fact]
    public void ARowWithoutCategory_StoresNullCategoryFields()
    {
        using var store = Seed(Item("Suelto", "111"));

        var stored = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Null(stored.CategoryId);
        Assert.Null(stored.CategoryName);
        Assert.Null(stored.CategoryIconKey);
    }

    [Fact]
    public void OpeningABranchDbCreatedBeforeCategories_AddsTheColumnsAndKeepsItsRows_Idempotently()
    {
        var legacyPresentation = Guid.NewGuid();
        using (var legacy = new SqliteConnection(ConnectionString))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = $"""
                CREATE TABLE catalog_replica (
                    presentation_id TEXT PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    product_id TEXT NOT NULL,
                    product_name TEXT NOT NULL,
                    presentation_name TEXT NOT NULL,
                    identification_code TEXT NULL,
                    quantity_behavior TEXT NOT NULL,
                    unit_id TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                INSERT INTO catalog_replica VALUES
                    ('{legacyPresentation}', '{_organizationId}', '{Guid.NewGuid()}', 'Harina', '1kg', '999',
                     'Integral', '{Guid.NewGuid()}', '2024-01-01T00:00:00.0000000+00:00');
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (var first = new BranchSyncStore(ConnectionString))
        {
            var legacyRow = Assert.Single(first.ListCatalogPriceReplica());
            Assert.Equal(legacyPresentation, legacyRow.PresentationId);
            Assert.Null(legacyRow.CategoryId);
            first.ApplyCatalogPriceSync([Item("Bife", "111", Meat, "Carnes", "meat")], [], DateTimeOffset.UtcNow);
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Equal(2, reopened.ListCatalogPriceReplica().Count);
        Assert.Contains(reopened.ListCatalogPriceReplica(), r => r.CategoryName == "Carnes");
    }

    [Fact]
    public void ListCatalogCategories_ReturnsTheDistinctLocalCategoriesByName_ForTheOrganizationOnly()
    {
        using var store = Seed(
            Item("Vino tinto", "1", Wine, "Vinos", "wine"),
            Item("Bife", "2", Meat, "Carnes", "meat"),
            Item("Chorizo", "3", Meat, "Carnes", "meat"),
            Item("Suelto", "4"),
            Item("Ajeno", "5", Guid.NewGuid(), "Ajena", "generic", organizationId: Guid.NewGuid()));

        var categories = store.ListCatalogCategories(_organizationId);

        Assert.Equal(["Carnes", "Vinos"], categories.Select(c => c.Name));
        Assert.Equal(["meat", "wine"], categories.Select(c => c.IconKey));
        Assert.Equal([Meat, Wine], categories.Select(c => c.Id));
    }

    [Fact]
    public void SearchCatalog_FiltersByCategory_AndCombinesWithTheNameSearch()
    {
        using var store = Seed(
            Item("Bife de chorizo", "1", Meat, "Carnes", "meat"),
            Item("Costilla", "2", Meat, "Carnes", "meat"),
            Item("Vino chorizo", "3", Wine, "Vinos", "wine"),
            Item("Suelto", "4"));

        Assert.Equal(4, store.SearchCatalog(_organizationId, null).Items.Count);
        Assert.Equal(4, store.SearchCatalog(_organizationId, null, categoryId: null).Items.Count);
        Assert.Equal(["Bife de chorizo", "Costilla"],
            store.SearchCatalog(_organizationId, null, categoryId: Meat).Items.Select(i => i.ProductName));
        Assert.Equal(["Bife de chorizo"],
            store.SearchCatalog(_organizationId, "chorizo", categoryId: Meat).Items.Select(i => i.ProductName));
        Assert.Empty(store.SearchCatalog(_organizationId, "costilla", categoryId: Wine).Items);
    }

    // --- Rail items (POS view model) -------------------------------------------

    [Fact]
    public void RailItems_ListTodosFirst_ThenEachCategoryWithItsGlyph()
    {
        var items = CategoryRailItem.Build(
        [
            new CatalogCategory(Meat, "Carnes", "meat"),
            new CatalogCategory(Wine, "Vinos", "wine"),
        ]);

        Assert.Equal(["Todos", "Carnes", "Vinos"], items.Select(i => i.Name));
        Assert.Null(items[0].Key);
        Assert.Equal(Meat.ToString(), items[1].Key);
        Assert.Equal(CategoryGlyphs.For("meat"), items[1].Glyph);
        Assert.Equal(CategoryGlyphs.For("wine"), items[2].Glyph);
    }

    [Fact]
    public void RailItems_WithNoCategories_AreJustTodos()
    {
        var items = CategoryRailItem.Build([]);

        Assert.Equal("Todos", Assert.Single(items).Name);
    }

    [Fact]
    public void Glyphs_CoverEveryIconKeyOfTheFixedSet_AndFallBackToGenericForAnUnknownKey()
    {
        string[] keys = ["meat", "poultry", "fish", "wine", "drinks", "charcoal", "grocery", "cleaning", "bakery", "dairy", "produce", "generic"];

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrEmpty(CategoryGlyphs.For(key)), key);
        }
        Assert.Equal(CategoryGlyphs.For("generic"), CategoryGlyphs.For("spaceship"));
        Assert.Equal(CategoryGlyphs.For("generic"), CategoryGlyphs.For(null));
        Assert.Equal(keys.Length, keys.Select(CategoryGlyphs.For).Distinct().Count());
    }
}
