using System.Net;
using System.Text;
using Commerce.Application.Audit;
using Commerce.Application.Access;
using Commerce.BranchNode;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T4, POS side: the client of `GET /device/pricelists/sync` and its place in the ONE sync sweep
/// (<see cref="SyncRunner"/>). A failed pull (unreachable, non-2xx, empty body) leaves the replica and cursor
/// byte-identical, so a stale replica keeps pricing offline.
/// </summary>
public sealed class PriceListsReplicaClientTests : IDisposable
{
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Bola = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-pricelists-client-{Guid.NewGuid():N}.db");

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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static PriceListsReplicaClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://stub.local") });

    private static object Component(string code, decimal percentage, int order) =>
        new { code, label = code, percentage, calculationBase = "Base", order };

    private static string Body(bool withReparto = true) => System.Text.Json.JsonSerializer.Serialize(
        new
        {
            lists = new List<object>
            {
                new { id = Mostrador, name = "Mostrador", isDefault = true, floorPriceListId = withReparto ? Reparto : (Guid?)null },
            }.Concat(withReparto ? [new { id = Reparto, name = "Reparto", isDefault = false, floorPriceListId = (Guid?)null }] : []),
            entries = new List<object>
            {
                new { priceListId = Mostrador, presentationId = Bola, unitPrice = 11_400m, effectiveFrom = "2026-10-01" },
            }.Concat(withReparto ? [new { priceListId = Reparto, presentationId = Bola, unitPrice = 11_400m, effectiveFrom = "2026-10-01" }] : []),
            rateSets = new List<object>
            {
                new
                {
                    id = Guid.NewGuid(), priceListId = (Guid?)Mostrador, effectiveFrom = "2026-10-01",
                    components = new[] { Component("IVA", 10.5m, 1), Component("IB", 2.5m, 2), Component("REMARCACION", 35m, 3) },
                },
            }.Concat(withReparto
                ?
                [
                    new
                    {
                        id = Guid.NewGuid(), priceListId = (Guid?)Reparto, effectiveFrom = "2026-10-01",
                        components = new[] { Component("IVA", 10.5m, 1), Component("IB", 2.5m, 2), Component("FLETE", 7m, 3), Component("REMARCACION", 25m, 4) },
                    },
                ]
                : []),
            customerPriceLists = new[] { new { customerId = Customer, priceListId = Reparto } }.Where(_ => withReparto),
            organizationDefaultCustomerPriceListId = withReparto ? Reparto : (Guid?)null,
            serverTimeUtc = "2026-10-02T18:30:00+00:00",
            // null: what a cloud from before customer discounts on the POS sends (the field absent reads the same).
            customerDiscounts = withReparto ? new[] { new { customerId = Customer, discountPercentage = 12.5m } } : null,
        },
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [Fact]
    public async Task Pull_SendsTheBearer_AndParsesTheWholeSnapshot()
    {
        var handler = new StubHandler(_ => Json(Body()));

        var outcome = await Client(handler).PullAsync("device-token");

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Snapshot!.Lists.Count);
        Assert.Equal(2, outcome.Snapshot.Entries.Count);
        Assert.Equal(2, outcome.Snapshot.RateSets.Count);
        Assert.Equal(4, outcome.Snapshot.RateSets.Single(s => s.PriceListId == Reparto).Components.Count);
        Assert.Equal((Customer, Reparto), (outcome.Snapshot.CustomerPriceLists.Single().CustomerId, outcome.Snapshot.CustomerPriceLists.Single().PriceListId));
        Assert.Equal(Reparto, outcome.Snapshot.OrganizationDefaultCustomerPriceListId);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 30, 0, TimeSpan.Zero), outcome.ServerTimeUtc);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/device/pricelists/sync", request.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("device-token", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task Pull_NonSuccess_CarriesNoData(int status)
    {
        var outcome = await Client(new StubHandler(_ => Json("{}", (HttpStatusCode)status))).PullAsync("t");

        Assert.False(outcome.Success);
        Assert.Null(outcome.Snapshot);
    }

    private static SyncRunner NewRunner(BranchSyncStore store, DevicePairing pairing, PriceListsReplicaClient priceLists)
    {
        var auditSink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(auditSink), auditSink);
        var unreachable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        return new SyncRunner(
            store, service, new CloudSyncClient(unreachable), new CustomerReplicaClient(unreachable),
            new CatalogPriceReplicaClient(unreachable), new OperatorProvisioningClient(unreachable),
            new LocalOperatorStore(Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json")),
            () => pairing, discountPinReplicaClient: null, stockReplicaClient: null, priceListsReplicaClient: priceLists);
    }

    private static DevicePairing Pairing() => new(Guid.NewGuid(), Guid.NewGuid(), "Branch", "op@example.com", "token");

    [Fact]
    public async Task TheSweep_PullsThePriceListsIntoTheReplica_AndAdvancesTheCursor()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var runner = NewRunner(store, Pairing(), Client(new StubHandler(_ => Json(Body()))));

        await runner.RunAsync(SyncTrigger.Startup);
        await runner.RunAsync(SyncTrigger.Timer); // redelivery is idempotent

        Assert.Equal(2, store.ListPriceLists().Count);
        Assert.Equal(16_872m, store.GetEffectiveRateSet(Mostrador, Today)!.Compose(11_400m));
        Assert.Equal(16_530m, store.GetEffectiveRateSet(Reparto, Today)!.Compose(11_400m));
        Assert.Equal(Reparto, store.GetCustomerPriceListId(Customer));
        Assert.Equal(12.5m, store.GetCustomerDiscountPercentage(Customer));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 30, 0, TimeSpan.Zero), store.GetPriceListsCursor());
    }

    [Fact]
    public async Task TheSweep_ARemovedListInTheCloud_DisappearsFromTheReplica()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var withReparto = true;
        var runner = NewRunner(store, Pairing(), Client(new StubHandler(_ => Json(Body(withReparto)))));
        await runner.RunAsync(SyncTrigger.Startup);

        withReparto = false;
        await runner.RunAsync(SyncTrigger.Timer);

        Assert.Equal("Mostrador", Assert.Single(store.ListPriceLists()).Name);
        Assert.Null(store.GetEffectivePrice(Reparto, Bola, Today));
        Assert.Null(store.GetCustomerPriceListId(Customer));
        Assert.Null(store.GetCustomerDiscountPercentage(Customer)); // a snapshot without discounts clears them
    }

    [Fact]
    public async Task TheSweep_AFailedPull_LeavesTheReplicaAndCursorUntouched_AndNeverThrows()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var good = true;
        var runner = NewRunner(store, Pairing(),
            Client(new StubHandler(_ => good ? Json(Body()) : Json("{}", HttpStatusCode.InternalServerError))));
        await runner.RunAsync(SyncTrigger.Startup);
        var cursor = store.GetPriceListsCursor();

        good = false;
        var result = await runner.RunAsync(SyncTrigger.Timer);

        Assert.NotNull(result);
        Assert.Equal(2, store.ListPriceLists().Count);
        Assert.Equal(16_530m, store.GetEffectiveRateSet(Reparto, Today)!.Compose(11_400m));
        Assert.Equal(cursor, store.GetPriceListsCursor());
    }
}
