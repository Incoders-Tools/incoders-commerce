using System.Net;
using System.Text;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// operator-ux-adjustments T5, POS side: the organization's quantity decimal separator reaches the terminal through the
/// sync sweep (`GET /device/organization/settings`), is stored in `branch.db` as the last known value (offline-safe), and
/// a failed or invalid answer keeps whatever was known. Never synced = null (the terminal culture formats, as before).
/// </summary>
public sealed class OrganizationSettingsReplicaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-org-settings-{Guid.NewGuid():N}.db");

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

    private static OrganizationSettingsReplicaClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://stub.local") });

    // ---- store --------------------------------------------------------------

    [Fact]
    public void Store_NeverSynced_HasNoSeparator()
    {
        using var store = new BranchSyncStore(ConnectionString);

        Assert.Null(store.GetQuantityDecimalSeparator());
    }

    [Theory]
    [InlineData("Comma")]
    [InlineData("Dot")]
    public void Store_KeepsTheLastKnownSeparator_AcrossAReopen(string separator)
    {
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.ApplyOrganizationSettings(separator == "Dot" ? "Comma" : "Dot");
            store.ApplyOrganizationSettings(separator);
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Equal(separator, reopened.GetQuantityDecimalSeparator());
    }

    // ---- client -------------------------------------------------------------

    [Fact]
    public async Task Pull_SendsTheBearerToTheDeviceRoute_AndReadsTheSeparator()
    {
        var handler = new StubHandler(_ => Json("""{"quantityDecimalSeparator":"Dot"}"""));

        var outcome = await Client(handler).PullAsync("device-token");

        Assert.True(outcome.Success);
        Assert.Equal("Dot", outcome.QuantityDecimalSeparator);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/device/organization/settings", request.RequestUri!.PathAndQuery);
        Assert.Equal(("Bearer", "device-token"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"quantityDecimalSeparator":"Dot"}""")]
    [InlineData(HttpStatusCode.OK, """{"quantityDecimalSeparator":"Semicolon"}""")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task Pull_AFailureOrAnUnknownValue_CarriesNoSeparator(HttpStatusCode status, string body)
    {
        var outcome = await Client(new StubHandler(_ => Json(body, status))).PullAsync("t");

        Assert.False(outcome.Success);
        Assert.Null(outcome.QuantityDecimalSeparator);
    }

    // ---- sweep --------------------------------------------------------------

    private static SyncRunner NewRunner(BranchSyncStore store, OrganizationSettingsReplicaClient settings)
    {
        var auditSink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(auditSink), auditSink);
        var unreachable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        var pairing = new DevicePairing(Guid.NewGuid(), Guid.NewGuid(), "Branch", "op@example.com", "token");
        return new SyncRunner(
            store, service, new CloudSyncClient(unreachable), new CustomerReplicaClient(unreachable),
            new CatalogPriceReplicaClient(unreachable), new OperatorProvisioningClient(unreachable),
            new LocalOperatorStore(Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json")),
            () => pairing, organizationSettingsReplicaClient: settings);
    }

    [Fact]
    public async Task TheSweep_StoresTheSeparator_AndTheNextSweepFollowsAChangeInTheWeb()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var separator = "Dot";
        var runner = NewRunner(store, Client(new StubHandler(_ => Json($$"""{"quantityDecimalSeparator":"{{separator}}"}"""))));

        await runner.RunAsync(SyncTrigger.Startup);
        Assert.Equal("Dot", store.GetQuantityDecimalSeparator());

        separator = "Comma";
        await runner.RunAsync(SyncTrigger.Timer);
        Assert.Equal("Comma", store.GetQuantityDecimalSeparator());
    }

    [Fact]
    public async Task TheSweep_AFailedPull_KeepsTheLastKnownSeparator_AndNeverThrows()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var good = true;
        var runner = NewRunner(store, Client(new StubHandler(_ => good
            ? Json("""{"quantityDecimalSeparator":"Dot"}""")
            : throw new HttpRequestException("down"))));
        await runner.RunAsync(SyncTrigger.Startup);

        good = false;
        var result = await runner.RunAsync(SyncTrigger.Timer);

        Assert.NotNull(result);
        Assert.Equal("Dot", store.GetQuantityDecimalSeparator());
    }
}
