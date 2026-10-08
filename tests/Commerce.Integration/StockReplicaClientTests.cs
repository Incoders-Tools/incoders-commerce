using System.Net;
using System.Text;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// purchases-receptions-and-stock T5: the POS client of `GET /device/stock/sync` and its place in the ONE sync sweep
/// (<see cref="SyncRunner"/>). A failed pull (unreachable, non-2xx, empty body) leaves the replica and cursor
/// byte-identical, so a stale replica stays usable offline.
/// </summary>
public sealed class StockReplicaClientTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-stock-client-{Guid.NewGuid():N}.db");

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

    private static StockReplicaClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://stub.local") });

    [Fact]
    public async Task Pull_SendsTheCursorAndTheBearer_AndParsesTheSnapshots()
    {
        var presentation = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(
            $$"""{"items":[{"presentationId":"{{presentation}}","onHand":117.5}],"serverTimeUtc":"2026-10-02T18:30:00+00:00"}"""));
        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = await Client(handler).PullAsync(since, "device-token");

        Assert.True(outcome.Success);
        var item = Assert.Single(outcome.Items!);
        Assert.Equal((presentation, 117.5m), (item.PresentationId, item.OnHand));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 30, 0, TimeSpan.Zero), outcome.ServerTimeUtc);
        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("/device/stock/sync?since=", request.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("device-token", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    public async Task Pull_NonSuccess_CarriesNoData(int status)
    {
        var outcome = await Client(new StubHandler(_ => Json("{}", (HttpStatusCode)status))).PullAsync(DateTimeOffset.MinValue, "t");

        Assert.False(outcome.Success);
        Assert.Null(outcome.Items);
    }

    private static SyncRunner NewRunner(BranchSyncStore store, DevicePairing pairing, StockReplicaClient stock)
    {
        var auditSink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(auditSink), auditSink);
        var unreachable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        return new SyncRunner(
            store, service, new CloudSyncClient(unreachable), new CustomerReplicaClient(unreachable),
            new CatalogPriceReplicaClient(unreachable), new OperatorProvisioningClient(unreachable),
            new LocalOperatorStore(Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json")),
            () => pairing, discountPinReplicaClient: null, stockReplicaClient: stock);
    }

    private static DevicePairing Pairing() => new(Guid.NewGuid(), Guid.NewGuid(), "Branch", "op@example.com", "token");

    [Fact]
    public async Task TheSweep_PullsStockIntoTheReplica_AndAdvancesTheCursor_ThenPullsIncrementally()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var presentation = Guid.NewGuid();
        var handler = new StubHandler(_ => Json(
            $$"""{"items":[{"presentationId":"{{presentation}}","onHand":12}],"serverTimeUtc":"2026-10-02T18:30:00+00:00"}"""));
        var runner = NewRunner(store, Pairing(), Client(handler));

        await runner.RunAsync(SyncTrigger.Startup);
        await runner.RunAsync(SyncTrigger.Timer);

        Assert.Equal(12m, store.GetStockOnHand([presentation])[presentation]);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 18, 30, 0, TimeSpan.Zero), store.GetStockCursor());
        Assert.Contains("since=0001", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("since=2026-10-02T18%3A30%3A00", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task TheSweep_AFailedStockPull_LeavesTheReplicaAndCursorUntouched_AndNeverThrows()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var presentation = Guid.NewGuid();
        var oldCursor = DateTimeOffset.UtcNow.AddDays(-2);
        store.ApplyStockSync([new StockReplicaItem(presentation, 40m)], oldCursor);
        var runner = NewRunner(store, Pairing(), Client(new StubHandler(_ => Json("{}", HttpStatusCode.InternalServerError))));

        var result = await runner.RunAsync(SyncTrigger.Timer);

        Assert.NotNull(result);
        Assert.Equal(40m, store.GetStockOnHand([presentation])[presentation]);
        Assert.Equal(oldCursor, store.GetStockCursor());
    }
}
