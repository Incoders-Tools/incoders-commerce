using System.Globalization;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The customer's own discount at the POS: it arrives with the `price-lists` snapshot and the shared
/// <see cref="PricingResolutionService"/> applies it AFTER the list composition, as the cloud does (ADR-010: same compiled
/// code on both channels). Vaca Verde figures: Bola de lomo base 11.400; Mostrador x 1,48 = 16.872 (walk-in); Reparto x 1,45
/// = 16.530; a Reparto customer with 12,5 % pays 16.530 x 0,875 = 14.463,75.
/// </summary>
public sealed class PosCustomerDiscountTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Lengua = Guid.NewGuid();
    private static readonly Guid DiscountedCustomer = Guid.NewGuid();
    private static readonly Guid PlainCustomer = Guid.NewGuid();
    private static readonly Guid ListlessDiscountedCustomer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly DiscountAuthorization Auth = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 3);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-customer-discount-{Guid.NewGuid():N}.db");

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

    private static PriceListsReplicaSnapshot Snapshot(IReadOnlyList<CustomerDiscountReplica>? discounts) => new(
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
        [new CustomerPriceListReplica(DiscountedCustomer, Reparto), new CustomerPriceListReplica(PlainCustomer, Reparto)],
        OrganizationDefaultCustomerPriceListId: Mostrador,
        discounts);

    private static readonly IReadOnlyList<CustomerDiscountReplica> Discounts =
    [
        new(DiscountedCustomer, 12.5m),
        new(ListlessDiscountedCustomer, 10m),
        new(PlainCustomer, 0m), // a zero discount is no discount
    ];

    private BranchSyncStore SyncedStore(IReadOnlyList<CustomerDiscountReplica>? discounts = null)
    {
        var store = new BranchSyncStore($"Data Source={_dbPath}");
        store.ApplyPriceListsSync(Snapshot(discounts ?? Discounts), DateTimeOffset.UtcNow);
        return store;
    }

    private static SaleCart CartOver(BranchSyncStore store)
    {
        var legacy = new PricingResolutionService(new LocalEffectivePriceSource(store));
        return new SaleCart(new BuyerPricingFactory(store, legacy).For, () => Today);
    }

    private static CatalogPriceReplicaItem Item(Guid presentationId, string name) => new(
        presentationId, Org, Guid.NewGuid(), name, "kg", "code-" + name, "Weighted", Guid.NewGuid(), null, null, DateTimeOffset.UtcNow);

    private static string Percent(decimal value) => $"{value.ToString("0.##", CultureInfo.CurrentCulture)} %";

    [Fact]
    public void TheSnapshot_KeepsEachCustomersOwnDiscount_AndDropsAZero()
    {
        using var store = SyncedStore();

        Assert.Equal(12.5m, store.GetCustomerDiscountPercentage(DiscountedCustomer));
        Assert.Equal(10m, store.GetCustomerDiscountPercentage(ListlessDiscountedCustomer));
        Assert.Null(store.GetCustomerDiscountPercentage(PlainCustomer));
    }

    [Fact]
    public void ANewSnapshot_ReplacesTheDiscounts_AndOneFromAnOlderCloud_LeavesNone()
    {
        using var store = SyncedStore();

        store.ApplyPriceListsSync(Snapshot([new CustomerDiscountReplica(PlainCustomer, 5m)]), DateTimeOffset.UtcNow);
        Assert.Null(store.GetCustomerDiscountPercentage(DiscountedCustomer));
        Assert.Equal(5m, store.GetCustomerDiscountPercentage(PlainCustomer));

        store.ApplyPriceListsSync(Snapshot(discounts: null), DateTimeOffset.UtcNow);
        Assert.Null(store.GetCustomerDiscountPercentage(PlainCustomer));
    }

    [Fact]
    public async Task ACustomerWithItsOwnDiscount_PaysItsListPriceLessTheDiscount_InTheLineAndTheCard()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        Assert.True((await cart.SetCustomerAsync(DiscountedCustomer)).Succeeded);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 2m);

        Assert.Equal((14_463.75m, 28_927.50m), (cart.Lines.Single().UnitPrice, cart.Total)); // 16.530 x 0,875
        Assert.Equal(14_463.75m, await cart.QuoteUnitPriceAsync(Bola));
        Assert.Equal(12.5m, cart.CustomerDiscountPercent);
        Assert.Equal($"Desc. cliente {Percent(12.5m)}", cart.CustomerDiscountShortText);
        Assert.StartsWith($"Descuento del cliente: {Percent(12.5m)} sobre los precios de la lista Reparto.", cart.CustomerDiscountNote);
        Assert.False(cart.HasDiscount); // it is the customer's price, not an operator discount needing a PIN
    }

    [Fact]
    public async Task ChangingTheBuyer_SwapsTheDiscount_OnEveryLine_BothWays()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);
        Assert.Equal(16_872m, cart.Total);

        Assert.True((await cart.SetCustomerAsync(DiscountedCustomer)).Succeeded);
        Assert.Equal(14_463.75m, cart.Total);

        Assert.True((await cart.SetCustomerAsync(PlainCustomer)).Succeeded);
        Assert.Equal(16_530m, cart.Total);
        Assert.Null(cart.CustomerDiscountPercent);
        Assert.Null(cart.CustomerDiscountNote);

        Assert.True((await cart.SetCustomerAsync(null)).Succeeded);
        Assert.Equal(16_872m, cart.Total);
        Assert.Null(cart.CustomerDiscountShortText);
    }

    [Fact]
    public async Task TheDiscount_AlsoAppliesToALinePricedByTheFallbackList()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(DiscountedCustomer);

        await cart.AddAsync(Item(Lengua, "Lengua"), 1m); // only Mostrador prices it: 7.817,57 x 1,48 = 11.570

        Assert.Equal(10_123.75m, cart.Lines.Single().UnitPrice); // 11.570 x 0,875
        Assert.Equal("Mostrador", cart.Lines.Single().FallbackListName);
    }

    [Fact]
    public async Task ACustomerWithoutAListOfItsOwn_GetsItsDiscountOverTheDefaultCustomerList()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(ListlessDiscountedCustomer);

        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        Assert.Equal(15_184.80m, cart.Lines.Single().UnitPrice); // Mostrador 16.872 x 0,90
        Assert.Contains("lista Mostrador", cart.CustomerDiscountNote);
    }

    [Fact]
    public async Task AnOperatorLineDiscount_AppliesOnTopOfTheCustomersPrice()
    {
        using var store = SyncedStore();
        var cart = CartOver(store);
        await cart.SetCustomerAsync(DiscountedCustomer);
        await cart.AddAsync(Item(Bola, "Bola de lomo"), 1m);

        cart.SetLineDiscount(Bola, 10m, Auth);

        Assert.Equal(1_446.38m, cart.Lines.Single().LineDiscountAmount); // 10 % of 14.463,75, half away from zero
        Assert.Equal(13_017.37m, cart.Total);
    }
}
