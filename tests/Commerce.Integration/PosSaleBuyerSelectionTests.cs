using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists L1: the cart owns the buyer of the sale and the customer picker only reflects it
/// (<see cref="SaleBuyerSelection"/>), so clearing the sale, a refused change or a customer that vanished from the replica
/// can never leave the picker showing somebody the cart is not pricing for.
/// </summary>
public sealed class PosSaleBuyerSelectionTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Lengua = Guid.NewGuid();
    private static readonly Guid RepartoCustomer = Guid.NewGuid();
    private static readonly Guid OtherCustomer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-buyer-selection-{Guid.NewGuid():N}.db");

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

    private static PriceListsReplicaSnapshot Snapshot(bool mostradorSellsBola, bool mostradorSellsLengua)
    {
        var entries = new List<PriceListEntryReplica> { new(Reparto, Bola, 11_400m, new DateOnly(2026, 10, 1)) };
        if (mostradorSellsBola)
        {
            entries.Add(new PriceListEntryReplica(Mostrador, Bola, 11_400m, new DateOnly(2026, 10, 1)));
        }

        if (mostradorSellsLengua)
        {
            entries.Add(new PriceListEntryReplica(Mostrador, Lengua, 7_817.57m, new DateOnly(2026, 10, 1)));
        }

        return new PriceListsReplicaSnapshot(
            Org,
            [new PriceListReplica(Mostrador, "Mostrador", true, Reparto), new PriceListReplica(Reparto, "Reparto", false, null)],
            entries,
            [
                new RateSetReplica(Guid.NewGuid(), Mostrador, new DateOnly(2026, 10, 1), [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("REMARCACION", 35m, 3)]),
                new RateSetReplica(Guid.NewGuid(), Reparto, new DateOnly(2026, 10, 1), [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("FLETE", 7m, 3), C("REMARCACION", 25m, 4)]),
            ],
            [new CustomerPriceListReplica(RepartoCustomer, Reparto), new CustomerPriceListReplica(OtherCustomer, Reparto)],
            Reparto);
    }

    private BranchSyncStore SyncedStore()
    {
        var store = new BranchSyncStore($"Data Source={_dbPath}");
        store.ApplyPriceListsSync(Snapshot(true, true), DateTimeOffset.UtcNow);
        return store;
    }

    private static SaleCart CartOver(BranchSyncStore store) =>
        new(new BuyerPricingFactory(store, new PricingResolutionService(new LocalEffectivePriceSource(store))).For, () => Today);

    private static CustomerReplica Customer(Guid id, string name) =>
        new(id, Org, name, "Individual", null, null, null, DateTimeOffset.UtcNow);

    private static CatalogPriceReplicaItem Item(Guid presentationId, string name) => new(
        presentationId, Org, Guid.NewGuid(), name, "kg", "code-" + name, "Weighted", Guid.NewGuid(), null, null, DateTimeOffset.UtcNow);

    private static IReadOnlyList<CustomerReplica> BothCustomers() =>
        [Customer(RepartoCustomer, "Carlos"), Customer(OtherCustomer, "Zulema")];

    [Fact]
    public async Task ItStartsOnWalkIn_AndChoosingACustomerMovesTheCartAndThePicker()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        Assert.Equal(0, selection.SelectedIndex);

        await selection.ChooseAsync(RepartoCustomer);

        Assert.Equal(RepartoCustomer, cart.CustomerId);
        Assert.Equal(RepartoCustomer, selection.Items[selection.SelectedIndex].CustomerId);
        Assert.Equal("Lista: Reparto", cart.PriceListLabel);
        Assert.Null(selection.Message);
    }

    [Fact]
    public async Task ClearingTheSale_ResetsThePickerTheCartAndTheListLabelToWalkIn()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        await selection.ChooseAsync(RepartoCustomer);
        await cart.AddAsync(Item(Bola, "Bola de lomo"));

        cart.Clear();

        Assert.Null(cart.CustomerId);
        Assert.Equal(0, selection.SelectedIndex);
        Assert.Equal(SaleCustomerPicker.WalkInLabel, selection.Items[selection.SelectedIndex].Label);
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
    }

    [Fact]
    public async Task ACustomerThatVanishesFromTheReplica_FallsBackToWalkInInThePickerAndTheCart_WithAMessage()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        await selection.ChooseAsync(RepartoCustomer);
        await cart.AddAsync(Item(Bola, "Bola de lomo"));
        Assert.Equal(16_530m, cart.Lines.Single().UnitPrice);

        await selection.RefreshAsync([Customer(OtherCustomer, "Zulema")]);

        Assert.Null(cart.CustomerId);
        Assert.Null(selection.Items[selection.SelectedIndex].CustomerId);
        Assert.Equal(16_872m, cart.Lines.Single().UnitPrice); // re-priced from Mostrador
        Assert.Equal("Lista: Mostrador", cart.PriceListLabel);
        Assert.Contains("Carlos", selection.Message);
    }

    [Fact]
    public async Task ARefreshThatKeepsTheCustomer_KeepsThePickAndRaisesNoMessage()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        await selection.ChooseAsync(OtherCustomer);

        await selection.RefreshAsync(BothCustomers());

        Assert.Equal(OtherCustomer, cart.CustomerId);
        Assert.Equal(OtherCustomer, selection.Items[selection.SelectedIndex].CustomerId);
        Assert.Null(selection.Message);
    }

    [Fact]
    public async Task ARefusedChange_KeepsThePickerOnTheBuyerTheCartStillHas_AndSaysWhy()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        await cart.AddAsync(Item(Lengua, "Lengua"));
        // Reparto never sold Lengua and the default list stops selling it: the customer change cannot price the line.
        store.ApplyPriceListsSync(Snapshot(true, false), DateTimeOffset.UtcNow);

        await selection.ChooseAsync(RepartoCustomer);

        Assert.Null(cart.CustomerId);
        Assert.Equal(0, selection.SelectedIndex);
        Assert.Contains("Lengua", selection.Message);
    }

    [Fact]
    public async Task AVanishedCustomerThatWalkInCannotPrice_StaysVisibleInThePickerSoItNeverDesyncs()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        var selection = new SaleBuyerSelection(cart);
        await selection.RefreshAsync(BothCustomers());
        await selection.ChooseAsync(RepartoCustomer);
        await cart.AddAsync(Item(Bola, "Bola de lomo"));
        // The default list stops selling Bola (Reparto still does): walk-in cannot take over this sale.
        store.ApplyPriceListsSync(Snapshot(false, true), DateTimeOffset.UtcNow);

        await selection.RefreshAsync([Customer(OtherCustomer, "Zulema")]);

        Assert.Equal(RepartoCustomer, cart.CustomerId);
        Assert.Equal(RepartoCustomer, selection.Items[selection.SelectedIndex].CustomerId);
        Assert.False(string.IsNullOrEmpty(selection.Message));
    }
}
