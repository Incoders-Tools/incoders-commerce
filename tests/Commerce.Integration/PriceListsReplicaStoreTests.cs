using Commerce.BranchNode;
using Commerce.Domain.Pricing;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T4, branch node side: the `price-lists` replica (every list's base prices, rate component sets,
/// list metadata, each customer's list, the organization default customer list). It is a snapshot applied in ONE
/// transaction with the cursor: redelivery is idempotent, a removal in the cloud disappears here, and an interrupted
/// apply leaves the replica and the cursor byte-identical.
/// </summary>
public sealed class PriceListsReplicaStoreTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly DateTimeOffset Cursor = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-pricelists-{Guid.NewGuid():N}.db");

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

    private static RateComponentReplica C(string code, decimal percentage, int order) =>
        new(code, code, percentage, "Base", order);

    private static PriceListsReplicaSnapshot Snapshot(bool withReparto = true) => new(
        Org,
        [
            new PriceListReplica(Mostrador, "Mostrador", true, withReparto ? Reparto : null),
            .. withReparto ? [new PriceListReplica(Reparto, "Reparto", false, null)] : Array.Empty<PriceListReplica>(),
        ],
        [
            new PriceListEntryReplica(Mostrador, Bola, 11_400m, new DateOnly(2026, 10, 1)),
            .. withReparto ? [new PriceListEntryReplica(Reparto, Bola, 11_400m, new DateOnly(2026, 10, 1))] : Array.Empty<PriceListEntryReplica>(),
        ],
        [
            new RateSetReplica(Guid.NewGuid(), Mostrador, new DateOnly(2026, 10, 1),
                [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("REMARCACION", 35m, 3)]),
            .. withReparto
                ? [new RateSetReplica(Guid.NewGuid(), Reparto, new DateOnly(2026, 10, 1),
                    [C("IVA", 10.5m, 1), C("IB", 2.5m, 2), C("FLETE", 7m, 3), C("REMARCACION", 25m, 4)])]
                : Array.Empty<RateSetReplica>(),
        ],
        withReparto ? [new CustomerPriceListReplica(Customer, Reparto)] : [],
        withReparto ? Reparto : null);

    [Fact]
    public void AnAppliedSnapshot_PricesAndComposesFromAnyList()
    {
        using var store = new BranchSyncStore(ConnectionString);

        store.ApplyPriceListsSync(Snapshot(), Cursor);

        Assert.Equal(["Mostrador", "Reparto"], store.ListPriceLists().Select(l => l.Name).Order());
        Assert.Equal(Reparto, store.ListPriceLists().Single(l => l.Name == "Mostrador").FloorPriceListId);
        Assert.Equal(11_400m, store.GetEffectivePrice(Mostrador, Bola, Today));
        Assert.Equal(11_400m, store.GetEffectivePrice(Reparto, Bola, Today));
        Assert.Equal(16_872m, store.GetEffectiveRateSet(Mostrador, Today)!.Compose(11_400m));
        Assert.Equal(16_530m, store.GetEffectiveRateSet(Reparto, Today)!.Compose(11_400m));
        Assert.Equal(Reparto, store.GetCustomerPriceListId(Customer));
        Assert.Equal(Reparto, store.GetOrganizationDefaultCustomerPriceListId());
        Assert.Equal(Cursor, store.GetPriceListsCursor());
    }

    [Fact]
    public void APriceNotYetEffective_IsNotAPrice_AndNoPriceIsNull()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyPriceListsSync(Snapshot(), Cursor);

        Assert.Null(store.GetEffectivePrice(Mostrador, Bola, new DateOnly(2026, 9, 30)));
        Assert.Null(store.GetEffectivePrice(Mostrador, Guid.NewGuid(), Today));
        Assert.Null(store.GetCustomerPriceListId(Guid.NewGuid()));
    }

    [Fact]
    public void ARateSet_IsTheListsOwnLatestOnOrBeforeTheDate_ElseTheOrganizationDefault_ElseNone()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var own = Snapshot() with
        {
            RateSets =
            [
                new RateSetReplica(Guid.NewGuid(), Mostrador, new DateOnly(2026, 10, 1), [C("REMARCACION", 35m, 1)]),
                new RateSetReplica(Guid.NewGuid(), Mostrador, new DateOnly(2026, 10, 10), [C("REMARCACION", 40m, 1)]),
                new RateSetReplica(Guid.NewGuid(), null, new DateOnly(2026, 10, 1), [C("IVA", 10.5m, 1)]),
            ],
        };
        store.ApplyPriceListsSync(own, Cursor);

        Assert.Equal(35m, store.GetEffectiveRateSet(Mostrador, Today)!.Components.Single().Percentage);
        Assert.Equal(40m, store.GetEffectiveRateSet(Mostrador, new DateOnly(2026, 10, 10))!.Components.Single().Percentage);
        // Reparto declares no set of its own: the organization default applies, all or nothing.
        var inherited = store.GetEffectiveRateSet(Reparto, Today)!;
        Assert.Equal("IVA", inherited.Components.Single().Code);
        Assert.True(inherited.IsOrganizationDefault);
        // Nothing effective before the first publication, for the list or the organization.
        Assert.Null(store.GetEffectiveRateSet(Mostrador, new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void ARedeliveredSnapshot_IsIdempotent()
    {
        using var store = new BranchSyncStore(ConnectionString);

        store.ApplyPriceListsSync(Snapshot(), Cursor);
        store.ApplyPriceListsSync(Snapshot(), Cursor.AddMinutes(1));

        Assert.Equal(2, store.ListPriceLists().Count);
        Assert.Equal(16_872m, store.GetEffectiveRateSet(Mostrador, Today)!.Compose(11_400m));
        Assert.Equal(Cursor.AddMinutes(1), store.GetPriceListsCursor());
    }

    [Fact]
    public void ARemovedList_DisappearsWithItsPricesSetsAndCustomerAssignments()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyPriceListsSync(Snapshot(), Cursor);

        store.ApplyPriceListsSync(Snapshot(withReparto: false), Cursor.AddMinutes(1));

        Assert.Equal("Mostrador", Assert.Single(store.ListPriceLists()).Name);
        Assert.Null(store.GetEffectivePrice(Reparto, Bola, Today));
        Assert.Null(store.GetEffectiveRateSet(Reparto, Today));
        Assert.Null(store.GetCustomerPriceListId(Customer));
        Assert.Null(store.GetOrganizationDefaultCustomerPriceListId());
        Assert.Null(store.ListPriceLists().Single().FloorPriceListId);
    }

    [Fact]
    public void AnInterruptedApply_LeavesTheReplicaAndTheCursorByteIdentical()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyPriceListsSync(Snapshot(), Cursor);

        store.SimulateInterruptedPriceListsSync(Snapshot(withReparto: false), Cursor.AddHours(1));

        Assert.Equal(2, store.ListPriceLists().Count);
        Assert.Equal(16_530m, store.GetEffectiveRateSet(Reparto, Today)!.Compose(11_400m));
        Assert.Equal(Cursor, store.GetPriceListsCursor());
    }

    [Fact]
    public void TheReplicaSurvivesAReopen_AndNeverSyncedMeansNoLists()
    {
        using (var first = new BranchSyncStore(ConnectionString))
        {
            Assert.Empty(first.ListPriceLists());
            Assert.Null(first.GetPriceListsCursor());
            first.ApplyPriceListsSync(Snapshot(), Cursor);
        }

        using var reopened = new BranchSyncStore(ConnectionString);

        Assert.Equal(2, reopened.ListPriceLists().Count);
        Assert.Equal(Cursor, reopened.GetPriceListsCursor());
    }
}
