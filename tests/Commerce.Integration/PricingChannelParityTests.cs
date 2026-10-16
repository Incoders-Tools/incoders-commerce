using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Pricing;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 3.8 (RED) / 3.9 (GREEN): the
/// ADR-010 channel-parity requirement, proven for the two sources this Unit
/// actually ships — <see cref="PostgresEffectivePriceSource"/> (cloud) and a
/// minimal SQLite-backed <see cref="IEffectivePriceSource"/> stub standing
/// in for the POS's future replica-backed source (the full
/// <c>LocalEffectivePriceSource</c> over `BranchSyncStore` ships in Phase 7
/// — this test only needs a second, independently-implemented source
/// reading the SAME tuple to prove the resolution service treats both
/// identically). If Postgres is not reachable, this test reports the gap
/// clearly and returns without asserting pass/fail, matching the existing
/// fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PricingChannelParityTests : IDisposable
{
    /// <summary>
    /// Test-only stand-in for the POS's SQLite-backed price source. Reads a
    /// single `price_replica`-shaped table by (presentation, effective date)
    /// using the SAME "latest effective_from on or before the date" rule as
    /// the Postgres source, over a temp on-disk SQLite database.
    /// </summary>
    private sealed class SqliteEffectivePriceSourceStub : IEffectivePriceSource, IDisposable
    {
        private readonly SqliteConnection _connection;

        public SqliteEffectivePriceSourceStub()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var create = _connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE price_replica (presentation_id TEXT NOT NULL, unit_price TEXT NOT NULL, effective_from TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        public void Seed(Guid presentationId, decimal unitPrice, DateOnly effectiveFrom)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO price_replica (presentation_id, unit_price, effective_from) VALUES ($p, $u, $e)";
            insert.Parameters.AddWithValue("$p", presentationId.ToString());
            insert.Parameters.AddWithValue("$u", unitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$e", effectiveFrom.ToString("yyyy-MM-dd"));
            insert.ExecuteNonQuery();
        }

        public Task<decimal?> GetUnitPriceAsync(Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
        {
            using var select = _connection.CreateCommand();
            select.CommandText =
                """
                SELECT unit_price FROM price_replica
                WHERE presentation_id = $p AND effective_from <= $e
                ORDER BY effective_from DESC
                LIMIT 1
                """;
            select.Parameters.AddWithValue("$p", presentationId.ToString());
            select.Parameters.AddWithValue("$e", effectiveOn.ToString("yyyy-MM-dd"));

            var raw = select.ExecuteScalar();
            return Task.FromResult(raw is null
                ? (decimal?)null
                : decimal.Parse((string)raw, System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Dispose() => _connection.Dispose();
    }

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PricingChannelParityTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        PostgresTestFixture.ApplyPricingMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();


    private static void SeedOrganization(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>B7 U4: catalog scopes now need a real branch row.</summary>
    private static Guid SeedBranch(Guid organizationId)
    {
        var branchId = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", owner);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
        return branchId;
    }

    /// <summary>
    /// Spec scenario "Same tuple resolves identically across channels": the
    /// SAME `(presentation, quantity, discount, date)` tuple resolved
    /// through a Postgres-backed source and an independently-implemented
    /// SQLite-backed source produces a byte-identical <c>Resolved</c> value
    /// — there is no channel parameter for either call site to differ on.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_PostgresAndSqliteSources_SameTuple_ByteIdenticalResult()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var branchId = SeedBranch(organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product", CategoryFixture.Create(scope), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        var effectiveFrom = new DateOnly(2026, 1, 1);
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentation.Id, 249.99m, effectiveFrom, "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var postgresSource = new PostgresEffectivePriceSource(priceStore, scope, priceList.Id);
        using var sqliteSource = new SqliteEffectivePriceSourceStub();
        sqliteSource.Seed(presentation.Id, 249.99m, effectiveFrom);

        var postgresService = new PricingResolutionService(postgresSource);
        var sqliteService = new PricingResolutionService(sqliteSource);

        var resolveOn = new DateOnly(2026, 3, 1);
        var postgresOutcome = await postgresService.ResolveAsync(presentation.Id, 4m, 10m, resolveOn, CancellationToken.None);
        var sqliteOutcome = await sqliteService.ResolveAsync(presentation.Id, 4m, 10m, resolveOn, CancellationToken.None);

        var postgresResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(postgresOutcome);
        var sqliteResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(sqliteOutcome);

        Assert.Equal(postgresResolved, sqliteResolved);
        Assert.Equal(224.99m, postgresResolved.UnitNetPrice);
        Assert.Equal(899.96m, postgresResolved.LineTotal);
    }

    /// <summary>
    /// commerce-price-composition spec scenario "Same tuple composes
    /// identically across channels": the same parity argument, now over the
    /// COMPOSED price. The Postgres side reads Vaca Verde's four components
    /// through the real <see cref="PostgresRateComponentSource"/>; the second
    /// channel is given the SAME domain set through an independently written
    /// source. Both produce 10,600 -> 15,370 -> a 10% discount, byte-identical,
    /// because composition lives in the one shared compiled method and neither
    /// call site has a channel parameter to differ on.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WithRateComponents_PostgresAndSecondChannel_ComposeByteIdentically()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var branchId = SeedBranch(organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Asado completo", CategoryFixture.Create(scope), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Kg", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Reparto", true, actorId), "org-user", actorId, CancellationToken.None);

        var effectiveFrom = new DateOnly(2026, 1, 1);
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentation.Id, 10600m, effectiveFrom, "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        IReadOnlyList<RateComponent> components =
        [
            new("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4),
        ];

        var componentStore = new PostgresRateComponentStore(_dataSource!);
        await componentStore.PublishSetAsync(
            scope, new NewRateComponentSet(Guid.NewGuid(), priceList.Id, effectiveFrom, components, actorId),
            "org-user", actorId, CancellationToken.None);

        var postgresService = new PricingResolutionService(
            new PostgresEffectivePriceSource(priceStore, scope, priceList.Id),
            new PostgresRateComponentSource(componentStore, scope, priceList.Id));

        using var sqliteSource = new SqliteEffectivePriceSourceStub();
        sqliteSource.Seed(presentation.Id, 10600m, effectiveFrom);
        var secondChannelService = new PricingResolutionService(
            sqliteSource,
            new ReplicatedRateComponentSourceStub(
                RateComponentSet.ForPriceList(Guid.NewGuid(), organizationId, priceList.Id, effectiveFrom, components)));

        var resolveOn = new DateOnly(2026, 3, 1);
        var postgresOutcome = await postgresService.ResolveAsync(presentation.Id, 2m, 10m, resolveOn, CancellationToken.None);
        var secondOutcome = await secondChannelService.ResolveAsync(presentation.Id, 2m, 10m, resolveOn, CancellationToken.None);

        var postgresResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(postgresOutcome);
        var secondResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(secondOutcome);

        Assert.Equal(postgresResolved, secondResolved);
        Assert.Equal(15370m, postgresResolved.UnitListPrice);
        Assert.Equal(13833m, postgresResolved.UnitNetPrice);
        Assert.Equal(27666m, postgresResolved.LineTotal);
    }

    /// <summary>
    /// customer-price-lists T2, the parity requirement over the BUYER's list: which list prices a sale is decided by
    /// the buyer alone (<see cref="BuyerPriceListSelector"/>), so the cloud (Postgres sources) and a second channel
    /// (independently written sources) pick the same list and compute byte-identical prices for the same buyer, with no
    /// channel parameter anywhere. A Reparto customer: 11.400 x 1,45 = 16.530; a walk-in: Mostrador, 11.400 x 1,48 = 16.872.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ForTheSameBuyer_PostgresAndSecondChannel_PickTheSameListAndPriceIdentically()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var branchId = SeedBranch(organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var actorId = Guid.NewGuid();

        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Bola de lomo", CategoryFixture.Create(scope), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Kg", QuantityBehavior.Weighted, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var componentStore = new PostgresRateComponentStore(_dataSource!);
        var effectiveFrom = new DateOnly(2026, 1, 1);

        async Task<Guid> NewList(string name, bool isDefault, params RateComponent[] components)
        {
            var list = await priceStore.CreatePriceListAsync(
                scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actorId), "org-user", actorId, CancellationToken.None);
            await priceStore.AppendEntryAsync(
                scope, new NewPriceListEntry(Guid.NewGuid(), list.Id, presentation.Id, 11400m, effectiveFrom, "Manual", null, actorId),
                "org-user", actorId, CancellationToken.None);
            await componentStore.PublishSetAsync(
                scope, new NewRateComponentSet(Guid.NewGuid(), list.Id, effectiveFrom, components, actorId),
                "org-user", actorId, CancellationToken.None);
            return list.Id;
        }

        RateComponent[] mostradorSet =
        [
            new("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new("REMARCACION", "Remarcación (35%)", 35m, RateCalculationBase.Base, 3),
        ];
        RateComponent[] repartoSet =
        [
            new("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4),
        ];
        var mostrador = await NewList("Mostrador", true, mostradorSet);
        var reparto = await NewList("Reparto", false, repartoSet);

        // Second channel: one independently written source per list, chosen with the same shared selector.
        using var sqliteSource = new SqliteEffectivePriceSourceStub();
        sqliteSource.Seed(presentation.Id, 11400m, effectiveFrom);
        var secondChannelSets = new Dictionary<Guid, RateComponentSet>
        {
            [mostrador] = RateComponentSet.ForPriceList(Guid.NewGuid(), organizationId, mostrador, effectiveFrom, mostradorSet),
            [reparto] = RateComponentSet.ForPriceList(Guid.NewGuid(), organizationId, reparto, effectiveFrom, repartoSet),
        };

        async Task<(Guid ListId, PriceResolutionOutcome.Resolved Cloud, PriceResolutionOutcome.Resolved Second)> Resolve(bool isCustomer, Guid? customerList)
        {
            var cloudList = (await priceStore.ResolveBuyerPriceListAsync(scope, isCustomer, customerList, CancellationToken.None))!;
            var cloud = new PricingResolutionService(
                new PostgresEffectivePriceSource(priceStore, scope, cloudList.Id),
                new PostgresRateComponentSource(componentStore, scope, cloudList.Id));

            var secondList = BuyerPriceListSelector.Select(isCustomer, customerList, null, mostrador)!.Value;
            var second = new PricingResolutionService(sqliteSource, new ReplicatedRateComponentSourceStub(secondChannelSets[secondList]));

            Assert.Equal(cloudList.Id, secondList);
            var on = new DateOnly(2026, 3, 1);
            return (secondList,
                Assert.IsType<PriceResolutionOutcome.Resolved>(await cloud.ResolveAsync(presentation.Id, 1m, null, on, CancellationToken.None)),
                Assert.IsType<PriceResolutionOutcome.Resolved>(await second.ResolveAsync(presentation.Id, 1m, null, on, CancellationToken.None)));
        }

        var customer = await Resolve(isCustomer: true, reparto);
        Assert.Equal(customer.Cloud, customer.Second);
        Assert.Equal(16530m, customer.Cloud.UnitListPrice);

        var walkIn = await Resolve(isCustomer: false, customerList: null);
        Assert.Equal(walkIn.Cloud, walkIn.Second);
        Assert.Equal(16872m, walkIn.Cloud.UnitListPrice);
    }

    /// <summary>
    /// customer-price-lists T6, parity of the FALLBACK: the cloud (Postgres sources) and the POS (branch replica through
    /// <see cref="BuyerPricingFactory"/>) price a Reparto customer's counter-only product (Lengua) identically from the
    /// default list with its own composition, mark it as fallback and name the same list; a product both lists price
    /// is not a fallback on either channel.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_FallbackToTheDefaultList_IsIdenticalInTheCloudAndThePos()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var branchId = SeedBranch(organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var actorId = Guid.NewGuid();
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        async Task<Guid> NewPresentation(string name)
        {
            var product = await catalogStore.CreateProductAsync(
                scope, new NewProduct(Guid.NewGuid(), name, CategoryFixture.Create(scope), Guid.NewGuid(), actorId), "org-user", actorId, CancellationToken.None);
            return (await catalogStore.CreatePresentationAsync(
                scope, new NewPresentation(Guid.NewGuid(), product.Id, "Kg", QuantityBehavior.Weighted, Guid.NewGuid(), null, actorId),
                "org-user", actorId, CancellationToken.None)).Id;
        }

        var bola = await NewPresentation("Bola de lomo");
        var lengua = await NewPresentation("Lengua");
        var priceStore = new PostgresPriceListStore(_dataSource!);
        var componentStore = new PostgresRateComponentStore(_dataSource!);
        var from = new DateOnly(2026, 1, 1);
        RateComponent[] mostradorSet = [new("IVA", "IVA", 10.5m, RateCalculationBase.Base, 1), new("REMARCACION", "Remarcación", 35m, RateCalculationBase.Base, 2)];
        RateComponent[] repartoSet = [new("IVA", "IVA", 10.5m, RateCalculationBase.Base, 1), new("FLETE", "Flete", 7m, RateCalculationBase.Base, 2), new("REMARCACION", "Remarcación", 25m, RateCalculationBase.Base, 3)];

        async Task<Guid> NewList(string name, bool isDefault, RateComponent[] set, params (Guid Presentation, decimal Base)[] entries)
        {
            var list = await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actorId), "org-user", actorId, CancellationToken.None);
            foreach (var (presentation, basePrice) in entries)
            {
                await priceStore.AppendEntryAsync(scope, new NewPriceListEntry(Guid.NewGuid(), list.Id, presentation, basePrice, from, "Manual", null, actorId), "org-user", actorId, CancellationToken.None);
            }
            await componentStore.PublishSetAsync(scope, new NewRateComponentSet(Guid.NewGuid(), list.Id, from, set, actorId), "org-user", actorId, CancellationToken.None);
            return list.Id;
        }

        var mostrador = await NewList("Mostrador", true, mostradorSet, (bola, 11_400m), (lengua, 7_817.57m));
        var reparto = await NewList("Reparto", false, repartoSet, (bola, 11_400m));
        var customer = Guid.NewGuid();

        // POS replica of the same two lists.
        var dbPath = Path.Combine(Path.GetTempPath(), $"parity-fallback-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new Commerce.BranchNode.BranchSyncStore($"Data Source={dbPath}");
            Commerce.BranchNode.RateComponentReplica C(RateComponent c) => new(c.Code, c.Label, c.Percentage, c.CalculationBase.ToString(), c.Order);
            store.ApplyPriceListsSync(new Commerce.BranchNode.PriceListsReplicaSnapshot(
                organizationId,
                [new Commerce.BranchNode.PriceListReplica(mostrador, "Mostrador", true, reparto), new Commerce.BranchNode.PriceListReplica(reparto, "Reparto", false, null)],
                [
                    new Commerce.BranchNode.PriceListEntryReplica(mostrador, bola, 11_400m, from),
                    new Commerce.BranchNode.PriceListEntryReplica(mostrador, lengua, 7_817.57m, from),
                    new Commerce.BranchNode.PriceListEntryReplica(reparto, bola, 11_400m, from),
                ],
                [
                    new Commerce.BranchNode.RateSetReplica(Guid.NewGuid(), mostrador, from, [.. mostradorSet.Select(C)]),
                    new Commerce.BranchNode.RateSetReplica(Guid.NewGuid(), reparto, from, [.. repartoSet.Select(C)]),
                ],
                [new Commerce.BranchNode.CustomerPriceListReplica(customer, reparto)],
                null), DateTimeOffset.UtcNow);
            var pos = new Commerce.Pos.Windows.BuyerPricingFactory(store, new PricingResolutionService(new Commerce.Pos.Windows.LocalEffectivePriceSource(store))).For(customer).Service;

            var cloudList = (await priceStore.ResolveBuyerPriceListAsync(scope, isCustomer: true, reparto, CancellationToken.None))!;
            var cloudDefault = (await priceStore.FindDefaultPriceListAsync(scope, CancellationToken.None))!;
            PriceListPorts Ports(Guid id) => new(id, new PostgresEffectivePriceSource(priceStore, scope, id), new PostgresRateComponentSource(componentStore, scope, id));
            var cloud = new PricingResolutionService(Ports(cloudList.Id), Ports(cloudDefault.Id));
            var on = new DateOnly(2026, 3, 1);

            foreach (var presentation in new[] { bola, lengua })
            {
                var fromCloud = Assert.IsType<PriceResolutionOutcome.Resolved>(await cloud.ResolveAsync(presentation, 2m, 10m, on, CancellationToken.None));
                var fromPos = Assert.IsType<PriceResolutionOutcome.Resolved>(await pos.ResolveAsync(presentation, 2m, 10m, on, CancellationToken.None));
                Assert.Equal(fromCloud, fromPos);
            }

            var lenguaResolved = Assert.IsType<PriceResolutionOutcome.Resolved>(await cloud.ResolveAsync(lengua, 1m, 0m, on, CancellationToken.None));
            Assert.True(lenguaResolved.FellBack);
            Assert.Equal(mostrador, lenguaResolved.PricedFromListId);
            Assert.False(Assert.IsType<PriceResolutionOutcome.Resolved>(await cloud.ResolveAsync(bola, 1m, 0m, on, CancellationToken.None)).FellBack);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(path)) { try { File.Delete(path); } catch (IOException) { } }
            }
        }
    }

    /// <summary>
    /// Test-only stand-in for a replica-backed component source, written
    /// independently of <see cref="PostgresRateComponentSource"/> so the parity
    /// assertion compares two implementations rather than one called twice.
    /// </summary>
    private sealed class ReplicatedRateComponentSourceStub(RateComponentSet set) : IEffectiveRateComponentSource
    {
        public Task<RateComponentSet?> GetEffectiveSetAsync(DateOnly effectiveOn, CancellationToken ct)
            => Task.FromResult(set.EffectiveFrom <= effectiveOn ? set : null);
    }
}
