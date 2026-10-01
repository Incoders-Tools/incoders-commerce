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
                         "0021_branch_codes.sql", "0010_guest_ordering.sql", "0025_orders.sql", "0026_orders_guest_check.sql",
                     })
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
            using var reset = new NpgsqlCommand("TRUNCATE TABLE order_lines, orders, guest_order_verifications, branches, organizations CASCADE", owner);
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
            Guid.NewGuid(), lines ?? [NewLine()], Guid.NewGuid(), destination, hasStock, verification: null, CancellationToken.None);

    private static Task<OrderSubmissionOutcome> SubmitGuestAsync(
        IOrderStore store, CloudTenantScope scope, Guid orderId, Guid branchId, GuestContact? contact = null,
        GuestVerificationConsumption? verification = null, IReadOnlyList<OrderLineSnapshot>? lines = null) =>
        store.SubmitAsync(
            scope, orderId, OrderOrigin.Guest, customerId: null,
            contact ?? new GuestContact("30111222", GuestContactChannel.Email, "guest@example.com", "Ana Guest", "Portón azul"),
            branchId, OrderActors.PublicGuest, lines ?? [NewLine(QuantityBehavior.FixedQuantity)], Guid.NewGuid(),
            destination: null, hasAvailableStock: false, verification, CancellationToken.None);

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

    // --- persist-web-orders T2: the guest verification is spent in the order transaction ---------

    private static readonly GuestContact VerifiedGuest =
        new("30111222333", GuestContactChannel.Email, "Guest@Example.com", "Ana Guest", null);

    /// <summary>Seeds a ticket the way the verification service leaves it: issued, then confirmed.</summary>
    private static async Task<GuestVerificationConsumption> SeedVerificationAsync(
        Guid orgId, bool confirmed = true, string documentId = "30111222333", string contactAddress = "guest@example.com",
        TimeSpan? confirmedAgo = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO guest_order_verifications (id, organization_id, document_id, contact_channel, contact_address,
                code_hash, expires_at, confirmed_at)
            VALUES ($1, $2, $3, 'Email', $4, 'hash', $5, $6)
            """, connection);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(orgId);
        cmd.Parameters.AddWithValue(documentId);
        cmd.Parameters.AddWithValue(contactAddress);
        cmd.Parameters.AddWithValue(now.AddMinutes(10));
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = confirmed ? now - (confirmedAgo ?? TimeSpan.Zero) : DBNull.Value,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
        });
        await cmd.ExecuteNonQueryAsync();
        return new GuestVerificationConsumption(id, VerifiedGuest.DocumentId, VerifiedGuest.ContactAddress, now.AddMinutes(-30));
    }

    private static async Task<(DateTimeOffset? ConsumedAt, Guid? OrderId)> ReadVerificationAsync(Guid verificationId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT consumed_at, consumed_order_id FROM guest_order_verifications WHERE id = $1", connection);
        cmd.Parameters.AddWithValue(verificationId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    [Fact]
    public async Task AGuestOrder_SpendsItsVerification_InTheSameTransactionThatStoresIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var ticket = await SeedVerificationAsync(org);
        var orderId = Guid.NewGuid();

        var outcome = await SubmitGuestAsync(NewStore(), scope, orderId, branch, VerifiedGuest, ticket);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        var (consumedAt, consumedOrder) = await ReadVerificationAsync(ticket.VerificationId);
        Assert.NotNull(consumedAt);
        Assert.Equal(orderId, consumedOrder);
    }

    [Fact]
    public async Task AFailedOrderInsert_LeavesTheVerificationUnconsumed_SoTheGuestCanRetry()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var ticket = await SeedVerificationAsync(org);
        var store = NewStore();
        var orderId = Guid.NewGuid();

        // The line insert fails after the order row was written and the ticket spent: all of it rolls back.
        await Assert.ThrowsAsync<PostgresException>(() =>
            SubmitGuestAsync(store, scope, orderId, branch, VerifiedGuest, ticket, lines: [NewLine((QuantityBehavior)99)]));

        var (consumedAt, consumedOrder) = await ReadVerificationAsync(ticket.VerificationId);
        Assert.Null(consumedAt);
        Assert.Null(consumedOrder);
        Assert.Null(await store.FindAsync(scope, orderId, CancellationToken.None));

        var retry = await SubmitGuestAsync(store, scope, orderId, branch, VerifiedGuest, ticket);
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, retry.Status);
        Assert.Equal(1, retry.Order!.OrderNumber!.Value.Sequence);
    }

    [Fact]
    public async Task ResubmittingAnExistingGuestOrder_ReturnsTheSameOrder_WithoutSpendingTheTicketAgain()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var ticket = await SeedVerificationAsync(org);
        var orderId = Guid.NewGuid();

        var first = await SubmitGuestAsync(NewStore(), scope, orderId, branch, VerifiedGuest, ticket);
        var (spentAt, _) = await ReadVerificationAsync(ticket.VerificationId);
        var again = await SubmitGuestAsync(NewStore(), scope, orderId, branch, VerifiedGuest, ticket);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, again.Status);
        Assert.False(again.WasNewlyAccepted);
        Assert.Equal(first.Order!.OrderNumber, again.Order!.OrderNumber);
        Assert.Equal(spentAt, (await ReadVerificationAsync(ticket.VerificationId)).ConsumedAt);

        // A different ticket (not the one that admitted this order) does not read the order back.
        var other = await SeedVerificationAsync(org);
        var foreign = await SubmitGuestAsync(NewStore(), scope, orderId, branch, VerifiedGuest, other);
        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, foreign.Status);
        Assert.Equal("verification-invalid", foreign.Reason);
        Assert.Null((await ReadVerificationAsync(other.VerificationId)).ConsumedAt);
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("expired-window")]
    [InlineData("other-document")]
    [InlineData("other-contact")]
    [InlineData("already-consumed")]
    public async Task AnUnusableVerification_DeniesTheOrder_StoresNothing_AndKeepsTheCounter(string defect)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var store = NewStore();
        var ticket = defect switch
        {
            "unconfirmed" => await SeedVerificationAsync(org, confirmed: false),
            "expired-window" => await SeedVerificationAsync(org, confirmedAgo: TimeSpan.FromMinutes(31)),
            "other-document" => await SeedVerificationAsync(org, documentId: "99999999"),
            "other-contact" => await SeedVerificationAsync(org, contactAddress: "someone-else@example.com"),
            _ => await SeedVerificationAsync(org),
        };
        if (defect == "already-consumed")
        {
            await SubmitGuestAsync(store, scope, Guid.NewGuid(), branch, VerifiedGuest, ticket);
        }
        var before = (await store.ListPendingAsync(scope, CancellationToken.None)).Count;
        var orderId = Guid.NewGuid();

        var outcome = await SubmitGuestAsync(store, scope, orderId, branch, VerifiedGuest, ticket);

        Assert.Equal(OrderSubmissionOutcomeStatus.Denied, outcome.Status);
        Assert.Equal("verification-invalid", outcome.Reason);
        Assert.Null(outcome.Order);
        Assert.Null(await store.FindAsync(scope, orderId, CancellationToken.None));
        Assert.Equal(before, (await store.ListPendingAsync(scope, CancellationToken.None)).Count);
        var next = await SubmitRegisteredAsync(store, scope, Guid.NewGuid(), branch);
        Assert.Equal(before + 1, next.Order!.OrderNumber!.Value.Sequence);
    }

    [Fact]
    public async Task AnUnknownDestinationBranch_DoesNotBurnTheGuestVerification()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var scope = new CloudTenantScope(org);
        var ticket = await SeedVerificationAsync(org);

        var outcome = await SubmitGuestAsync(NewStore(), scope, Guid.NewGuid(), Guid.NewGuid(), VerifiedGuest, ticket);

        Assert.Equal("destination-branch-not-found", outcome.Reason);
        Assert.Null((await ReadVerificationAsync(ticket.VerificationId)).ConsumedAt);
    }

    [Fact]
    public async Task OneVerification_AdmitsExactlyOneOrder_EvenWhenSubmissionsRace()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var ticket = await SeedVerificationAsync(org);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => SubmitGuestAsync(NewStore(), scope, Guid.NewGuid(), branch, VerifiedGuest, ticket))));

        Assert.Equal(1, outcomes.Count(o => o.Status == OrderSubmissionOutcomeStatus.Accepted));
        Assert.Equal(7, outcomes.Count(o => o.Reason == "verification-invalid"));
        Assert.Single(await NewStore().ListPendingAsync(scope, CancellationToken.None));
    }

    // --- review follow-ups (persist-web-orders) -----------------------------------------------------

    private static void DeleteSqlite(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path)) { try { File.Delete(path); } catch (IOException) { } }
        }
    }

    [Fact]
    public async Task TheSameOrderIdRacingThroughTwoBranches_StoresOneOrder_AndEveryCallerGetsIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        // Different branches take different advisory locks, so only the primary key serializes them:
        // the loser hits ON CONFLICT DO NOTHING, rolls back and re-reads the winner.
        var org = await SeedOrganizationAsync();
        var branchA = await SeedBranchAsync(org);
        var branchB = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => SubmitRegisteredAsync(NewStore(), scope, orderId, i % 2 == 0 ? branchA : branchB, customerId))));

        Assert.All(outcomes, o => Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, o.Status));
        Assert.Equal(1, outcomes.Count(o => o.WasNewlyAccepted));
        Assert.Single(outcomes.Select(o => o.Order!.OrderNumber!.Value.Format()).Distinct());
        Assert.Single(await NewStore().ListPendingAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task Readers_NeverSeeAnOrderWithoutItsLines_WhileOrdersAreBeingSubmitted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var store = NewStore();
        using var stop = new CancellationTokenSource();

        // Best-effort interleaving probe: reading lines and orders as two statements throws on an order
        // that committed in between (no lines to rehydrate). Orders first, then their lines, never does.
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var listed = await store.ListPendingAsync(scope, CancellationToken.None);
                Assert.All(listed, o => Assert.NotEmpty(o.Lines));
            }
        })).ToArray();

        for (var i = 0; i < 40; i++)
        {
            await SubmitRegisteredAsync(store, scope, Guid.NewGuid(), branch);
        }
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(40, (await store.ListPendingAsync(scope, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task APendingList_IsLimitedAndOnlyHoldsOrdersStillPendingForTheDestination()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var clock = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var tick = 0;
        var store = NewStore(() => clock.AddMinutes(tick++));
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(Guid.NewGuid());
            await SubmitRegisteredAsync(store, scope, ids[i], branch);
        }

        // One order is delivered: it is no longer "pending destination" and leaves the list.
        var dbPath = Path.Combine(Path.GetTempPath(), $"branch-pending-{Guid.NewGuid():N}.db");
        try
        {
            using var destination = new BranchSyncStore($"Data Source={dbPath}");
            await store.RetryDeliveryAsync(scope, ids[0], Guid.NewGuid(), Guid.NewGuid(), destination, hasAvailableStock: true, CancellationToken.None);
        }
        finally
        {
            DeleteSqlite(dbPath);
        }

        var all = await store.ListPendingAsync(scope, CancellationToken.None);
        var limited = await store.ListPendingAsync(scope, CancellationToken.None, limit: 2);

        Assert.Equal(ids.Skip(1), all.Select(o => o.OrderId));
        Assert.Equal(ids.Skip(1).Take(2), limited.Select(o => o.OrderId));
        Assert.Single(await store.ListPendingAsync(scope, CancellationToken.None, limit: 0));   // clamped up to 1
        Assert.Equal(200, IOrderStore.DefaultPendingLimit);
        Assert.Equal(500, IOrderStore.MaxPendingLimit);
    }

    [Fact]
    public async Task ADeliveryThatCannotBePersisted_NeverLeavesAPhantomOrderOnTheBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var orderId = Guid.NewGuid();
        var dbPath = Path.Combine(Path.GetTempPath(), $"branch-phantom-{Guid.NewGuid():N}.db");
        var guard = "orders_no_delivery_" + org.ToString("N");
        // A trigger that refuses every delivery-state UPDATE of this organization, so persisting the delivery fails.
        await ExecOwnerAsync(
            $"CREATE OR REPLACE FUNCTION {guard}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'delivery state refused'; END $$");
        await ExecOwnerAsync(
            $"CREATE TRIGGER {guard} BEFORE UPDATE OF status, pending_reason ON orders FOR EACH ROW WHEN (NEW.organization_id = '{org}') EXECUTE FUNCTION {guard}()");
        try
        {
            using var destination = new BranchSyncStore($"Data Source={dbPath}");
            var outcome = await SubmitRegisteredAsync(NewStore(), scope, orderId, branch, destination: destination, hasStock: true);

            // The order was committed BEFORE delivery was attempted: it exists, honestly pending, and
            // the branch inbox holds an order that exists (never a phantom from a rolled-back transaction).
            Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
            var stored = await NewStore().FindAsync(scope, orderId, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(OrderDeliveryStatus.PendingDestination, stored!.Status);
            Assert.Equal(OrderPendingReason.DestinationOffline, stored.PendingReason);
            Assert.Equal(OrderDeliveryStatus.PendingDestination, outcome.Order!.Status);

            // A later retry (trigger gone) reaches the same inbox entry idempotently and confirms.
            await ExecOwnerAsync($"DROP TRIGGER {guard} ON orders");
            var retried = await NewStore().RetryDeliveryAsync(
                scope, orderId, Guid.NewGuid(), Guid.NewGuid(), destination, hasAvailableStock: true, CancellationToken.None);
            Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, retried.Order!.Status);
        }
        finally
        {
            await ExecOwnerAsync($"DROP TRIGGER IF EXISTS {guard} ON orders");
            await ExecOwnerAsync($"DROP FUNCTION IF EXISTS {guard}()");
            DeleteSqlite(dbPath);
        }
    }

    [Fact]
    public async Task ARolledBackSubmission_NeverDeliversToTheBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var dbPath = Path.Combine(Path.GetTempPath(), $"branch-rollback-{Guid.NewGuid():N}.db");
        try
        {
            using var destination = new BranchSyncStore($"Data Source={dbPath}");
            // The line insert fails, so the transaction rolls back before any delivery.
            await Assert.ThrowsAsync<PostgresException>(() => SubmitRegisteredAsync(
                NewStore(), scope, Guid.NewGuid(), branch, lines: [NewLine((QuantityBehavior)99)], destination: destination, hasStock: true));

            await using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            await sqlite.OpenAsync();
            await using var count = sqlite.CreateCommand();
            count.CommandText = "SELECT COUNT(1) FROM inbound_orders";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
        finally
        {
            DeleteSqlite(dbPath);
        }
    }

    [Fact]
    public async Task TheDeliveredPayload_CarriesTheOrderNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var org = await SeedOrganizationAsync();
        var branch = await SeedBranchAsync(org);
        var scope = new CloudTenantScope(org);
        var dbPath = Path.Combine(Path.GetTempPath(), $"branch-number-{Guid.NewGuid():N}.db");
        try
        {
            using var destination = new BranchSyncStore($"Data Source={dbPath}");
            var orderId = Guid.NewGuid();
            var outcome = await SubmitRegisteredAsync(NewStore(), scope, orderId, branch, destination: destination, hasStock: true);
            Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, outcome.Order!.Status);

            await using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            await sqlite.OpenAsync();
            await using var read = sqlite.CreateCommand();
            read.CommandText = "SELECT payload FROM inbound_orders WHERE order_id = $id";
            read.Parameters.AddWithValue("$id", orderId.ToString());
            var payload = Commerce.Domain.Sync.SyncPayloadCodec.Deserialize<Commerce.Domain.Sync.Payloads.OrderPayloadV1>((string)(await read.ExecuteScalarAsync())!);

            Assert.Equal("P01-W-1", payload.OrderNumber);
        }
        finally
        {
            DeleteSqlite(dbPath);
        }
    }
}
