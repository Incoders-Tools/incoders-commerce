using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T4, POS pricing: the sale is priced from the list that applies to its BUYER, from the replica,
/// with the same compiled code as the cloud (<see cref="BuyerPriceListSelector"/> + <see cref="PricingResolutionService"/>).
/// Vaca Verde figures: Bola de lomo base 11.400, Mostrador x 1,48 = 16.872 (walk-in), Reparto x 1,45 = 16.530 (customer).
/// Rule for a customer change on an open sale: every line is re-priced from the new buyer's list at its current quantity;
/// line discount percentages and the sale discount (and their authorization) are kept and their amounts recomputed; a
/// change that would leave a line without a price in the new list is refused and nothing changes.
/// </summary>
public sealed class PosBuyerPricingTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Lengua = Guid.NewGuid();
    private static readonly Guid RepartoCustomer = Guid.NewGuid();
    private static readonly Guid CounterCustomer = Guid.NewGuid();
    private static readonly Guid UnassignedCustomer = Guid.NewGuid();
    private static readonly Guid GhostList = Guid.NewGuid();
    private static readonly Guid GhostCustomer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly DiscountAuthorization Auth = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 3);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-buyer-pricing-{Guid.NewGuid():N}.db");

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

    private static RateComponentReplica C(string code, decimal percentage, int order) => new(code, code, percentage, "Base", order);

    /// <summary>Mostrador (default, composed x 1,48, sells Bola AND Lengua) and Reparto (x 1,45, sells Bola only).</summary>
    private static PriceListsReplicaSnapshot Snapshot(Guid? orgDefaultCustomerList = null) => new(
        Org,
        [new PriceListReplica(Mostrador, "Mostrador", true, Reparto), new PriceListReplica(Reparto, "Reparto", false, null)],
        [
            new PriceListEntryReplica(Mostrador, Bola, 11_400m, new DateOnly(2026, 10, 1)),
            new PriceListEntryReplica(Mostrador, Lengua, 7_817.57m, new DateOnly(2026, 10, 1)),
            new PriceListEntryReplica(Reparto, Bola, 11_400m, new DateOnly(2026, 10, 1)),
        ],
        [
            new RateSetReplica(Guid.NewGuid(), Mostrador, new DateOnly(2026, 10, 1), [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("REMARCACION", 35m, 3)]),
            new RateSetReplica(Guid.NewGuid(), Reparto, new DateOnly(2026, 10, 1), [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("FLETE", 7m, 3), C("REMARCACION", 25m, 4)]),
        ],
        [
            new CustomerPriceListReplica(RepartoCustomer, Reparto),
            new CustomerPriceListReplica(CounterCustomer, Mostrador),
            new CustomerPriceListReplica(GhostCustomer, GhostList), // a list this branch does not have
        ],
        orgDefaultCustomerList);

    private BranchSyncStore SyncedStore(bool withOrgDefaultCustomerList = true)
    {
        var store = new BranchSyncStore($"Data Source={_dbPath}");
        store.ApplyPriceListsSync(Snapshot(withOrgDefaultCustomerList ? Reparto : null), DateTimeOffset.UtcNow);
        return store;
    }

    private static SaleCart CartOver(BranchSyncStore store)
    {
        var legacy = new PricingResolutionService(new LocalEffectivePriceSource(store));
        return new SaleCart(new BuyerPricingFactory(store, legacy).For, () => Today);
    }

    private static CatalogPriceReplicaItem Item(Guid presentationId, string name) => new(
        presentationId, Org, Guid.NewGuid(), name, "kg", "code-" + name, "Weighted", Guid.NewGuid(), null, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task AWalkInSale_IsPricedFromTheDefaultList_Mostrador_16872()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);

        var added = await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.True(added.Succeeded);
        Assert.Equal(16_872m, cart.Lines.Single().UnitPrice); // 11.400 x 1,48
        Assert.Equal("Mostrador", cart.PriceListName);
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
    }

    [Fact]
    public async Task TheSameSale_WithARepartoCustomerSelected_IsPricedAtReparto_16530()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        Assert.True((await cart.SetCustomerAsync(RepartoCustomer)).Succeeded);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(16_530m, cart.Lines.Single().UnitPrice); // 11.400 x 1,45
        Assert.Equal("Lista: Reparto", cart.PriceListLabel);
    }

    [Fact]
    public async Task ChangingTheCustomerOnAnOpenSale_RepricesEveryLineAtItsQuantity_BothWays()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);
        await cart.SetQuantityAsync(Bola, 2m);
        Assert.Equal(33_744m, cart.Total);

        var toReparto = await cart.SetCustomerAsync(RepartoCustomer);

        Assert.True(toReparto.Succeeded);
        Assert.Equal((16_530m, 33_060m), (cart.Lines.Single().UnitPrice, cart.Total));
        Assert.Equal(RepartoCustomer, cart.CustomerId);

        var backToWalkIn = await cart.SetCustomerAsync(null);

        Assert.True(backToWalkIn.Succeeded);
        Assert.Equal((16_872m, 33_744m), (cart.Lines.Single().UnitPrice, cart.Total));
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
    }

    [Fact]
    public async Task ACustomerChange_KeepsDiscountPercentagesAndTheirAuthorization_RecomputingTheAmounts()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);
        cart.SetLineDiscount(Bola, 10m, Auth);
        cart.SetSaleDiscount(5m, Auth);

        await cart.SetCustomerAsync(RepartoCustomer);

        var line = cart.Lines.Single();
        Assert.Equal((10m, 1_653m), (line.LineDiscountPercent, line.LineDiscountAmount)); // 10 % of 16.530
        Assert.Equal(5m, cart.SaleDiscountPercent);
        Assert.Equal(Auth, cart.Authorization);
        Assert.Equal(14_877m - 743.85m, cart.Total); // (16.530 - 1.653) less 5 %
    }

    [Fact]
    public async Task ACustomerChange_OnALineOnlyMostradorPrices_RepricesItFromMostrador_InsteadOfRefusing()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);
        await cart.AddAsync(Item(Lengua, "Lengua"), 1m); // sold only at the counter list

        var result = await cart.SetCustomerAsync(RepartoCustomer);

        Assert.True(result.Succeeded);
        Assert.Equal(RepartoCustomer, cart.CustomerId);
        Assert.Equal("Lista: Reparto", cart.PriceListLabel);
        Assert.Equal([16_530m, 11_570m], cart.Lines.Select(l => l.UnitPrice)); // Bola at Reparto; Lengua at Mostrador (7.817,57 x 1,48)
        Assert.Null(cart.Lines[0].FallbackListName);
        Assert.Equal("Mostrador", cart.Lines[1].FallbackListName);
        Assert.Equal("(precio de Mostrador)", cart.Lines[1].PriceNote);
        Assert.Equal(string.Empty, cart.Lines[0].PriceNote);
    }

    [Fact]
    public async Task AddingACounterOnlyProduct_ToACustomersSale_IsPricedFromMostrador_AndNoted()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(RepartoCustomer);

        var added = await cart.AddAsync(Item(Lengua, "Lengua"), 1m);

        Assert.True(added.Succeeded);
        Assert.Equal(11_570m, cart.Lines.Single().UnitPrice);
        Assert.Equal("(precio de Mostrador)", cart.Lines.Single().PriceNote);
    }

    [Fact]
    public async Task ACustomerChange_IsStillRefused_WhenNoListPricesALine()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);
        // A line of a product no list prices (e.g. a price that stops being effective after the sale was started).
        var unpriced = new ScannedSaleLineViewModel(Guid.NewGuid(), null, "Hueso", "kg", 1m, 100m, 100m);
        cart.Lines.Add(unpriced);

        var result = await cart.SetCustomerAsync(RepartoCustomer);

        Assert.False(result.Succeeded);
        Assert.Contains("Hueso", result.Message);
        Assert.Null(cart.CustomerId);
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
        Assert.Equal(16_872m, cart.Lines[0].UnitPrice); // untouched
    }

    [Fact]
    public async Task ACustomerWithoutAList_UsesTheOrganizationDefaultCustomerList()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(UnassignedCustomer);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(16_530m, cart.Lines.Single().UnitPrice);
        Assert.Equal("Lista: Reparto", cart.PriceListLabel);
    }

    [Fact]
    public async Task ACustomersListMissingFromTheReplica_FallsBackPerTheSelector_AndSaysSo()
    {
        using var store = SyncedStore(withOrgDefaultCustomerList: false);
        var cart = CartOver(store);
        await cart.SetCustomerAsync(GhostCustomer);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(16_872m, cart.Lines.Single().UnitPrice); // no org default customer list: the branch default
        Assert.Equal("Mostrador", cart.PriceListName);
        Assert.Contains("la lista del cliente no está disponible", cart.PriceListLabel);
    }

    [Fact]
    public async Task ACustomersListMissingFromTheReplica_FallsToTheOrganizationDefaultCustomerList_WhenThereIsOne()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(GhostCustomer);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(16_530m, cart.Lines.Single().UnitPrice);
        Assert.Contains("Reparto", cart.PriceListLabel);
        Assert.Contains("la lista del cliente no está disponible", cart.PriceListLabel);
    }

    [Fact]
    public async Task AStaleReplica_StillPrices_WithoutAnySync()
    {
        using (SyncedStore()) { }
        using var reopened = new BranchSyncStore($"Data Source={_dbPath}"); // a restart, offline
        var cart = CartOver(reopened);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(16_872m, cart.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task AReplicaThatNeverSyncedPriceLists_PricesFromTheSingleLegacyList_WithNoListLabel()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        store.ApplyCatalogPriceSync(
            [new CatalogPriceReplicaItem(Bola, Org, Guid.NewGuid(), "Bola de lomo", "kg", "code", "Weighted", Guid.NewGuid(), 11_400m, new DateOnly(2026, 10, 1), DateTimeOffset.UtcNow)],
            [], DateTimeOffset.UtcNow);
        var cart = CartOver(store);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(11_400m, cart.Lines.Single().UnitPrice);
        Assert.Null(cart.PriceListLabel);
    }

    [Fact]
    public async Task TheCatalogCardPrice_IsTheListPriceOfTheCurrentBuyer()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);

        Assert.Equal(16_872m, await cart.QuoteUnitPriceAsync(Bola));
        await cart.SetCustomerAsync(RepartoCustomer);
        Assert.Equal(16_530m, await cart.QuoteUnitPriceAsync(Bola));
        Assert.Equal(11_570m, await cart.QuoteUnitPriceAsync(Lengua)); // not in Reparto: the default list prices it
        Assert.Null(await cart.QuoteUnitPriceAsync(Guid.NewGuid()));   // no list prices it: never a zero
    }

    [Fact]
    public async Task ClearingTheSale_ReturnsToTheWalkInList()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(RepartoCustomer);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        cart.Clear();

        Assert.Null(cart.CustomerId);
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
    }

    [Fact]
    public void TheSaleScreen_ShowsTheListName_AndRepricesWhenTheCustomerPickerChanges()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Commerce.Pos.Windows", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(dir.FullName, "src", "Commerce.Pos.Windows", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PriceListText\"", xaml);
        Assert.Contains("SelectionChanged=\"CustomerPickerComboBox_SelectionChanged\"", xaml);
        Assert.Contains("_buyer.ChooseAsync(", code); // the cart owns the buyer, the picker reflects it (SaleBuyerSelection)
        Assert.Contains("PriceListText.Text = _cart.PriceListLabel", code);
        Assert.Contains("new BuyerPricingFactory(", code);
        Assert.Contains("{Binding PriceNote}", File.ReadAllText(Path.Combine(dir.FullName, "src", "Commerce.Pos.Windows", "Controls", "SaleLinesTable.xaml")));
    }
}
