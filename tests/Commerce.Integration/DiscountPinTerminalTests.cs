using System.Net;
using System.Text;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Pos.Windows;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// branch-discount-pin spec, terminal side: the verifier cached in
/// `branch.db` (idempotent migration), offline verification, the lockout that
/// survives a restart, and the replication client / sync runner that fill it.
/// </summary>
public sealed class DiscountPinTerminalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-discount-pin-{Guid.NewGuid():N}.db");
    private readonly Guid _branchId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();

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

    private DiscountPinReplica ReplicaFor(string pin, long version = 1) =>
        new(_branchId, version, BranchDiscountPin.Derive(pin), Now.AddDays(-1));

    private BranchPinDiscountAuthorizer NewAuthorizer(BranchSyncStore store, Func<DateTimeOffset>? clock = null) =>
        new(store, () => _branchId, clock ?? (() => Now));

    // ---- replica storage ----

    [Fact]
    public void Replica_RoundTrips_AndVerifiesOffline()
    {
        using var store = new BranchSyncStore(ConnectionString);
        Assert.Null(store.GetDiscountPin(_branchId));

        store.ApplyDiscountPin(_branchId, ReplicaFor("2468", version: 3));

        var cached = store.GetDiscountPin(_branchId)!;
        Assert.Equal(3, cached.Version);
        Assert.True(BranchDiscountPin.Verify("2468", cached.Verifier));
        Assert.False(BranchDiscountPin.Verify("0000", cached.Verifier));
    }

    [Fact]
    public void Replica_IsReplacedOnRotation_AndClearedWhenTheBranchHasNone()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468", 1));
        store.ApplyDiscountPin(_branchId, ReplicaFor("1357", 2));

        var cached = store.GetDiscountPin(_branchId)!;
        Assert.Equal(2, cached.Version);
        Assert.True(BranchDiscountPin.Verify("1357", cached.Verifier));

        store.ApplyDiscountPin(_branchId, null);
        Assert.Null(store.GetDiscountPin(_branchId));
    }

    [Fact]
    public void Replica_OfAnotherBranch_IsNeverReturned()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));

        Assert.Null(store.GetDiscountPin(Guid.NewGuid()));
    }

    [Fact]
    public void ReopeningTheDatabase_KeepsTheReplicaAndTheLockout()
    {
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
            store.SetDiscountPinLockout(new PinLockoutState(0, Now.AddMinutes(5)));
        }

        using var reopened = new BranchSyncStore(ConnectionString);

        Assert.NotNull(reopened.GetDiscountPin(_branchId));
        Assert.Equal(Now.AddMinutes(5), reopened.GetDiscountPinLockout().LockedUntilUtc);
    }

    // ---- authorizer ----

    [Fact]
    public void Authorizer_WithoutAPin_ReportsNotConfigured_AndGrantsNothing()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var authorizer = NewAuthorizer(store);

        Assert.Equal(DiscountAvailability.NotConfigured, authorizer.GetAvailability().State);
        var outcome = authorizer.Authorize("2468", _operatorId);
        Assert.Equal(DiscountAuthorizationKind.NotConfigured, outcome.Kind);
        Assert.Null(outcome.Authorization);
        Assert.Contains("no tiene un PIN", outcome.Message);
    }

    [Fact]
    public void Authorizer_CorrectPin_GrantsABranchPinAuthorizationForTheOperator()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468", version: 7));
        var authorizer = NewAuthorizer(store);

        var outcome = authorizer.Authorize("2468", _operatorId);

        Assert.Equal(DiscountAuthorizationKind.Granted, outcome.Kind);
        Assert.Equal(new DiscountAuthorization(DiscountAuthorization.BranchPin, _operatorId, 7), outcome.Authorization);
    }

    [Fact]
    public void Authorizer_WrongPin_IsDeniedAndCounted()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        var authorizer = NewAuthorizer(store);

        var outcome = authorizer.Authorize("0000", _operatorId);

        Assert.Equal(DiscountAuthorizationKind.Denied, outcome.Kind);
        Assert.Null(outcome.Authorization);
        Assert.Equal(4, outcome.AttemptsRemaining);
        Assert.Equal(1, store.GetDiscountPinLockout().FailedAttempts);
    }

    [Fact]
    public void Authorizer_BlankPin_AsksForItWithoutCountingAFailure()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        var authorizer = NewAuthorizer(store);

        var outcome = authorizer.Authorize("  ", _operatorId);

        Assert.Equal(DiscountAuthorizationKind.Denied, outcome.Kind);
        Assert.Equal(0, store.GetDiscountPinLockout().FailedAttempts);
    }

    [Fact]
    public void Authorizer_FifthFailureLocks_EvenTheRightPinIsRefused_AndTheLockSurvivesARestart()
    {
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
            var authorizer = NewAuthorizer(store);
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(DiscountAuthorizationKind.Denied, authorizer.Authorize("0000", _operatorId).Kind);
            }

            var fifth = authorizer.Authorize("0000", _operatorId);
            Assert.Equal(DiscountAuthorizationKind.Locked, fifth.Kind);
            Assert.Equal(TimeSpan.FromMinutes(5), fifth.LockedFor);

            var right = authorizer.Authorize("2468", _operatorId);
            Assert.Equal(DiscountAuthorizationKind.Locked, right.Kind);
            Assert.Null(right.Authorization);
        }

        // A new process: new store, new authorizer, same file.
        using var restarted = new BranchSyncStore(ConnectionString);
        var afterRestart = NewAuthorizer(restarted, () => Now.AddMinutes(2));
        Assert.Equal(DiscountAvailability.Locked, afterRestart.GetAvailability().State);
        Assert.Equal(DiscountAuthorizationKind.Locked, afterRestart.Authorize("2468", _operatorId).Kind);
    }

    [Fact]
    public void Authorizer_AfterTheLockExpires_TheRightPinIsAcceptedAndCountersReset()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        var locking = NewAuthorizer(store);
        for (var i = 0; i < 5; i++) locking.Authorize("0000", _operatorId);

        var later = NewAuthorizer(store, () => Now.AddMinutes(5));
        var outcome = later.Authorize("2468", _operatorId);

        Assert.Equal(DiscountAuthorizationKind.Granted, outcome.Kind);
        Assert.Equal(PinLockoutState.None, store.GetDiscountPinLockout());
    }

    [Fact]
    public void Authorizer_ACorrectPinResetsThePriorFailures()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        var authorizer = NewAuthorizer(store);
        for (var i = 0; i < 3; i++) authorizer.Authorize("0000", _operatorId);

        Assert.Equal(DiscountAuthorizationKind.Granted, authorizer.Authorize("2468", _operatorId).Kind);

        Assert.Equal(0, store.GetDiscountPinLockout().FailedAttempts);
    }

    [Fact]
    public void Authorizer_IsBehindAnInterfaceSoAnotherSourceCanBeAddedLater() =>
        Assert.IsAssignableFrom<IDiscountAuthorizer>(NewAuthorizer(new BranchSyncStore(ConnectionString)));

    // ---- older database ----

    [Fact]
    public void AnOlderBranchDatabaseWithoutTheDiscountTables_OpensAndUpgradesIdempotently()
    {
        // A database from before discounts: only the pre-existing sale tables.
        using (var raw = new SqliteConnection(ConnectionString))
        {
            raw.Open();
            using var create = raw.CreateCommand();
            create.CommandText = """
                CREATE TABLE sale_effects (
                    sale_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, total_amount TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL, sale_kind TEXT NOT NULL DEFAULT 'Manual', customer_id TEXT NULL);
                CREATE TABLE sale_lines (
                    sale_id TEXT NOT NULL, line_number INTEGER NOT NULL, presentation_id TEXT NOT NULL,
                    identification_code TEXT NULL, product_name TEXT NOT NULL, presentation_name TEXT NOT NULL,
                    quantity TEXT NOT NULL, unit_price TEXT NOT NULL, line_total TEXT NOT NULL,
                    PRIMARY KEY (sale_id, line_number));
                INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc) VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '10', '2026-01-01T00:00:00.0000000+00:00');
                """;
            create.ExecuteNonQuery();
        }

        using (var first = new BranchSyncStore(ConnectionString))
        {
            var old = first.GetSaleEffect(Guid.Parse("11111111-1111-1111-1111-111111111111"))!;
            Assert.Null(old.SaleDiscountPercent);
            Assert.Null(old.DiscountAuthorization);
        }

        using var second = new BranchSyncStore(ConnectionString);
        second.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        Assert.NotNull(second.GetDiscountPin(_branchId));
    }

    // ---- replication client and runner ----

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? LastBearer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            LastBearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string PinJson(DiscountPinVerifier verifier, long version) =>
        $$"""{"isSet":true,"version":{{version}},"algorithm":"{{verifier.Algorithm}}","iterations":{{verifier.Iterations}},"salt":"{{Convert.ToBase64String(verifier.Salt)}}","hash":"{{Convert.ToBase64String(verifier.Hash)}}","changedAtUtc":"2026-09-01T00:00:00Z"}""";

    [Fact]
    public async Task Client_ReadsTheBranchVerifierWithTheDeviceBearer()
    {
        var verifier = BranchDiscountPin.Derive("2468");
        var handler = new RoutingHandler(_ => Json(PinJson(verifier, 5)));
        var client = new DiscountPinReplicaClient(new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid") });

        var outcome = await client.PullAsync("device-token");

        Assert.True(outcome.Success);
        Assert.Equal("/device/branch/discount-pin", Assert.Single(handler.Paths));
        Assert.Equal("device-token", handler.LastBearer);
        var replica = outcome.ToReplica(_branchId)!;
        Assert.Equal(5, replica.Version);
        Assert.True(BranchDiscountPin.Verify("2468", replica.Verifier));
    }

    [Fact]
    public async Task Client_NoPinSet_YieldsNoReplica_AndAFailureYieldsNoOutcome()
    {
        var none = new DiscountPinReplicaClient(new HttpClient(new RoutingHandler(_ => Json("""{"isSet":false}"""))) { BaseAddress = new Uri("https://cloud.invalid") });
        var noneOutcome = await none.PullAsync("t");
        Assert.True(noneOutcome.Success);
        Assert.Null(noneOutcome.ToReplica(_branchId));

        var denied = new DiscountPinReplicaClient(new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))) { BaseAddress = new Uri("https://cloud.invalid") });
        Assert.False((await denied.PullAsync("t")).Success);

        var down = new DiscountPinReplicaClient(new HttpClient(new RoutingHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("https://cloud.invalid") });
        Assert.False((await down.PullAsync("t")).Success);
    }

    private SyncRunner NewRunner(BranchSyncStore store, RoutingHandler handler, DevicePairing pairing)
    {
        var auditSink = new InMemoryAuditSink();
        var branchNodeService = new BranchNodeService(store, new TenantAuthorizationService(auditSink), auditSink);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.invalid") };
        return new SyncRunner(
            store, branchNodeService, new CloudSyncClient(http), new CustomerReplicaClient(http),
            new CatalogPriceReplicaClient(http), new OperatorProvisioningClient(http),
            new LocalOperatorStore(Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json")),
            () => pairing, new DiscountPinReplicaClient(http));
    }

    private DevicePairing Pairing() => new(Guid.NewGuid(), _branchId, "Branch", "op@example.com", "token");

    [Fact]
    public async Task SyncRunner_CachesTheVerifier_AndKeepsItWhenTheCloudIsUnreachable()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var verifier = BranchDiscountPin.Derive("2468");
        var online = new RoutingHandler(r => r.RequestUri!.AbsolutePath == "/device/branch/discount-pin"
            ? Json(PinJson(verifier, 2))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await NewRunner(store, online, Pairing()).RunAsync(SyncTrigger.Button);

        Assert.True(BranchDiscountPin.Verify("2468", store.GetDiscountPin(_branchId)!.Verifier));

        var offline = new RoutingHandler(_ => throw new HttpRequestException("offline"));
        await NewRunner(store, offline, Pairing()).RunAsync(SyncTrigger.Button);

        Assert.NotNull(store.GetDiscountPin(_branchId));
    }

    [Fact]
    public async Task SyncRunner_DropsTheCachedVerifier_WhenTheCloudSaysNoneIsSet()
    {
        using var store = new BranchSyncStore(ConnectionString);
        store.ApplyDiscountPin(_branchId, ReplicaFor("2468"));
        var handler = new RoutingHandler(r => r.RequestUri!.AbsolutePath == "/device/branch/discount-pin"
            ? Json("""{"isSet":false}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await NewRunner(store, handler, Pairing()).RunAsync(SyncTrigger.Button);

        Assert.Null(store.GetDiscountPin(_branchId));
    }
}
