using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// persist-web-orders T1: orders live in Postgres. Every test runs against LIVE Postgres (skipped
/// when unreachable, like the other store tests) and reads state back through a NEW store instance
/// where the point is that it survives an API restart.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresOrderStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PostgresOrderStoreTests()
    {
        if (!_postgresAvailable) return;

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            foreach (var file in new[]
                     {
                         "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql",
                         "0021_branch_codes.sql", "0025_orders.sql",
                     })
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
            using var reset = new NpgsqlCommand("TRUNCATE TABLE order_lines, orders, branches, organizations CASCADE", owner);
            reset.ExecuteNonQuery();
        }
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private PostgresOrderStore NewStore(Func<DateTimeOffset>? clock = null) => new(_dataSource!, clock);

    private static async Task ExecOwnerAsync(string sql, params object[] args)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> SeedOrganizationAsync()
    {
        var orgId = Guid.NewGuid();
        await ExecOwnerAsync("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", orgId);
        return orgId;
    }

    private static async Task<Guid> SeedBranchAsync(Guid orgId)
    {
        var branchId = Guid.NewGuid();
        await ExecOwnerAsync("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", branchId, orgId, "Branch " + branchId);
        return branchId;
    }

    private static OrderLineSnapshot NewLine(QuantityBehavior behavior = QuantityBehavior.Weighted) => new(
        ProductId: Guid.NewGuid(), ProductName: "Harina 000 ñandú", PresentationId: Guid.NewGuid(), PresentationName: "Bolsa 25 kg",
        QuantityBehavior: behavior, UnitId: Guid.NewGuid(), Quantity: 2.5m,
        UnitListPrice: 1234.5678m, AppliedDiscountPercentage: 7.5m, UnitNetPrice: 1141.9363m, LineTotal: 2854.8408m);

    private static Task<OrderSubmissionOutcome> SubmitRegisteredAsync(
        IOrderStore store, CloudTenantScope scope, Guid orderId, Guid branchId, Guid? customerId = null,
        IReadOnlyList<OrderLineSnapshot>? lines = null, BranchSyncStore? destination = null, bool hasStock = false) =>
        store.SubmitAsync(
            scope, orderId, OrderOrigin.RegisteredCustomer, customerId ?? Guid.NewGuid(), guestContact: null, branchId,
            Guid.NewGuid(), lines ?? [NewLine()], Guid.NewGuid(), destination, hasStock, CancellationToken.None);

    private static Task<OrderSubmissionOutcome> SubmitGuestAsync(
        IOrderStore store, CloudTenantScope scope, Guid orderId, Guid branchId, GuestContact? contact = null) =>
        store.SubmitAsync(
            scope, orderId, OrderOrigin.Guest, customerId: null,
            contact ?? new GuestContact("30111222", GuestContactChannel.Email, "guest@example.com", "Ana Guest", "Portón azul"),
            branchId, OrderActors.PublicGuest, [NewLine(QuantityBehavior.FixedQuantity)], Guid.NewGuid(),
            destination: null, hasAvailableStock: false, CancellationToken.None);

    [Fact]
    public async Task ARegisteredOrder_IsStoredNumbered_AndSurvivesARestart()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var line = NewLine();

        var outcome = await SubmitRegisteredAsync(NewStore(), scope, orderId, branch, customerId, [line]);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.True(outcome.WasNewlyAccepted);
        Assert.Equal("P01-W-1", outcome.Order!.OrderNumber!.Value.Format());

        // "Restart": a brand-new store (and data source) reads everything back.
        using var restartedSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var found = await new PostgresOrderStore(restartedSource).FindAsync(scope, orderId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(orderId, found!.OrderId);
        Assert.Equal(org, found.OrganizationId);
        Assert.Equal(OrderOrigin.RegisteredCustomer, found.Origin);
        Assert.Equal(customerId, found.CustomerId);
        Assert.Null(found.GuestContact);
        Assert.Equal(branch, found.DestinationBranchId);
        Assert.Equal("P01-W-1", found.OrderNumber!.Value.Format());
        Assert.Equal(OrderDeliveryStatus.PendingDestination, found.Status);
        Assert.Equal(OrderPendingReason.DestinationOffline, found.PendingReason);
        Assert.Equal(outcome.Order.SubmittedAtUtc.UtcDateTime, found.SubmittedAtUtc.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(line, Assert.Single(found.Lines));
    }

    [Fact]
    public async Task AGuestOrder_RehydratesItsContact_AndKeepsTheInvariant()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var withNotes = Guid.NewGuid();
        var withoutNotes = Guid.NewGuid();

        await SubmitGuestAsync(NewStore(), scope, withNotes, branch);
        await SubmitGuestAsync(NewStore(), scope, withoutNotes, branch,
            new GuestContact("27999888", GuestContactChannel.Email, "otra@example.com", "Bea", DeliveryNotes: null));

        var restarted = NewStore();
        var first = (await restarted.FindAsync(scope, withNotes, CancellationToken.None))!;
        var second = (await restarted.FindAsync(scope, withoutNotes, CancellationToken.None))!;

        Assert.Equal(OrderOrigin.Guest, first.Origin);
        Assert.Null(first.CustomerId);
        Assert.Equal(new GuestContact("30111222", GuestContactChannel.Email, "guest@example.com", "Ana Guest", "Portón azul"), first.GuestContact);
        Assert.Null(second.GuestContact!.DeliveryNotes);
        Assert.Equal("27999888", second.GuestContact.DocumentId);
        Assert.Equal(QuantityBehavior.FixedQuantity, Assert.Single(first.Lines).QuantityBehavior);
        Assert.Equal(["P01-W-1", "P01-W-2"], new[] { first, second }.Select(o => o.OrderNumber!.Value.Format()));
    }

    [Fact]
    public async Task ResubmittingTheSameOrderId_ReturnsTheSameOrderAndNumber_WithoutAdvancingTheCounter()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var first = await SubmitRegisteredAsync(NewStore(), scope, orderId, branch, customerId);
        var again = await SubmitRegisteredAsync(NewStore(), scope, orderId, branch, customerId);
        var next = await SubmitRegisteredAsync(NewStore(), scope, Guid.NewGuid(), branch);

        Assert.True(first.WasNewlyAccepted);
        Assert.False(again.WasNewlyAccepted);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, again.Status);
        Assert.Equal("existing-order", again.Reason);
        Assert.Equal(first.Order!.OrderNumber, again.Order!.OrderNumber);
        Assert.Equal("P01-W-2", next.Order!.OrderNumber!.Value.Format());
    }

    [Fact]
    public async Task Sequences_AreCountedPerBranch_AndTheBranchCodeIsTheBranchesOwn()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branchOne = await SeedBranchAsync(org);   // code 01
        var branchTwo = await SeedBranchAsync(org);   // code 02
        var scope = new CloudTenantScope(org);
        var store = NewStore();

        var numbers = new List<string>();
        foreach (var branch in new[] { branchOne, branchOne, branchTwo, branchOne, branchTwo })
        {
            var outcome = await SubmitRegisteredAsync(store, scope, Guid.NewGuid(), branch);
            numbers.Add(outcome.Order!.OrderNumber!.Value.Format());
        }

        Assert.Equal(["P01-W-1", "P01-W-2", "P02-W-1", "P01-W-3", "P02-W-2"], numbers);
    }

    [Fact]
    public async Task ConcurrentSubmissionsToOneBranch_GetDistinctSequentialNumbers()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);

        // Separate store instances, like separate API processes sharing one database.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => SubmitRegisteredAsync(NewStore(), scope, Guid.NewGuid(), branch))));

        var sequences = outcomes.Select(o => o.Order!.OrderNumber!.Value.Sequence).OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, 20), sequences);
    }

    [Fact]
    public async Task ConcurrentSubmissionsOfTheSameOrderId_StoreOneOrder_AndAcceptItOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => SubmitRegisteredAsync(NewStore(), scope, orderId, branch, customerId))));

        Assert.All(outcomes, o => Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, o.Status));
        Assert.Equal(1, outcomes.Count(o => o.WasNewlyAccepted));
        Assert.Single(outcomes.Select(o => o.Order!.OrderNumber).Distinct());
        Assert.Single(await NewStore().ListPendingAsync(scope, CancellationToken.None));
        var next = await SubmitRegisteredAsync(NewStore(), scope, Guid.NewGuid(), branch);
        Assert.Equal(2, next.Order!.OrderNumber!.Value.Sequence);
    }

    [Fact]
    public async Task AnotherOrganization_CannotReadOrResubmitAnOrder_AndHasItsOwnSequences()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgA = await SeedOrganizationAsync();
        var branchA = await SeedBranchAsync(orgA);
        var orgB = await SeedOrganizationAsync();
        var branchB = await SeedBranchAsync(orgB);
        var scopeA = new CloudTenantScope(orgA);
        var scopeB = new CloudTenantScope(orgB);
        var store = NewStore();
        var orderId = Guid.NewGuid();

        await SubmitRegisteredAsync(store, scopeA, orderId, branchA);

        Assert.Null(await store.FindAsync(scopeB, orderId, CancellationToken.None));
        Assert.Empty(await store.ListPendingAsync(scopeB, CancellationToken.None));

        // The same business id in another organization is a different order (idempotency is per org).
        var other = await SubmitRegisteredAsync(store, scopeB, orderId, branchB);
        Assert.True(other.WasNewlyAccepted);
        Assert.Equal(orgB, other.Order!.OrganizationId);
        Assert.Equal(1, other.Order.OrderNumber!.Value.Sequence);

        // ...and another organization's branch is not a destination.
        var crossed = await SubmitRegisteredAsync(store, scopeB, Guid.NewGuid(), branchA);
        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, crossed.Status);
        Assert.Equal("destination-branch-not-found", crossed.Reason);
        Assert.Single(await store.ListPendingAsync(scopeA, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownDestinationBranch_IsATypedDenial_AndStoresNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var scope = new CloudTenantScope(org);
        var store = NewStore();

        var outcome = await SubmitRegisteredAsync(store, scope, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("destination-branch-not-found", outcome.Reason);
        Assert.Null(outcome.Order);
        Assert.False(outcome.WasNewlyAccepted);
        Assert.Empty(await store.ListPendingAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task ListPending_RanksRegisteredBeforeGuest_ThenBySubmissionTime()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var clock = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var store = NewStore(() => clock.AddMinutes(tick++));

        var guestEarly = Guid.NewGuid();
        var registeredFirst = Guid.NewGuid();
        var guestLate = Guid.NewGuid();
        var registeredSecond = Guid.NewGuid();
        await SubmitGuestAsync(store, scope, guestEarly, branch);
        await SubmitRegisteredAsync(store, scope, registeredFirst, branch);
        await SubmitGuestAsync(store, scope, guestLate, branch,
            new GuestContact("1", GuestContactChannel.Email, "late@example.com", "Late", null));
        await SubmitRegisteredAsync(store, scope, registeredSecond, branch);

        var listed = await NewStore().ListPendingAsync(scope, CancellationToken.None);

        Assert.Equal([registeredFirst, registeredSecond, guestEarly, guestLate], listed.Select(o => o.OrderId));
        Assert.All(listed, o => Assert.Single(o.Lines));
    }

    [Fact]
    public async Task DeliveryState_IsPersisted_WhenADeliveryAttemptChangesIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var dbPath = Path.Combine(Path.GetTempPath(), $"branch-order-store-{Guid.NewGuid():N}.db");
        try
        {
            using var destination = new BranchSyncStore($"Data Source={dbPath}");
            var store = NewStore();
            var stockUnknown = Guid.NewGuid();
            var delivered = Guid.NewGuid();

            await SubmitRegisteredAsync(store, scope, stockUnknown, branch, destination: destination, hasStock: false);
            await SubmitRegisteredAsync(store, scope, delivered, branch);

            var afterSubmit = await NewStore().FindAsync(scope, stockUnknown, CancellationToken.None);
            Assert.Equal(OrderPendingReason.StockUnconfirmed, afterSubmit!.PendingReason);

            var retried = await store.RetryDeliveryAsync(
                scope, delivered, Guid.NewGuid(), Guid.NewGuid(), destination, hasAvailableStock: true, CancellationToken.None);
            Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, retried.Status);

            var reread = await NewStore().FindAsync(scope, delivered, CancellationToken.None);
            Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, reread!.Status);
            Assert.Equal(OrderPendingReason.None, reread.PendingReason);

            var missing = await store.RetryDeliveryAsync(
                scope, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), destination, hasAvailableStock: true, CancellationToken.None);
            Assert.Equal(OrderSubmissionOutcomeStatus.Denied, missing.Status);
            Assert.Equal("not-found", missing.Reason);
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

    [Fact]
    public async Task AFailureWhileStoring_LeavesNoOrderAndDoesNotAdvanceTheCounter()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var store = NewStore();

        // An enum value the schema rejects fails the line insert, after the order row was written.
        var broken = NewLine((QuantityBehavior)99);
        await Assert.ThrowsAsync<PostgresException>(() =>
            SubmitRegisteredAsync(store, scope, Guid.NewGuid(), branch, lines: [broken]));

        Assert.Empty(await store.ListPendingAsync(scope, CancellationToken.None));
        var next = await SubmitRegisteredAsync(store, scope, Guid.NewGuid(), branch);
        Assert.Equal(1, next.Order!.OrderNumber!.Value.Sequence);
    }
}
