using System.Net;
using Commerce.BranchNode;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 6 task 6.4's process-integration
/// threat-matrix row (design.md "Process integration"): an unreachable host
/// or a 401 from `GET /device/catalog/sync` must leave the replica AND the
/// cursor completely unchanged — the same "sync-blocked, not sales-blocked"
/// guard <see cref="MainWindow.PullCatalogPricesAsync"/> implements
/// (`if (!outcome.Success ...) return;` before any
/// <see cref="BranchSyncStore.ApplyCatalogPriceSync"/> call). Because
/// <c>MainWindow</c> itself is not unit-testable (WPF; see
/// <see cref="PosCompositionRootTests"/>'s doc comment), this test exercises
/// <see cref="CatalogPriceReplicaClient"/> against a fake
/// <see cref="HttpMessageHandler"/> and replicates that exact guard shape
/// against a real <see cref="BranchSyncStore"/>, proving the store is never
/// touched on failure.
/// </summary>
public sealed class CatalogPriceReplicationProcessIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-catalog-prices-proc-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the OS temp directory is periodically reclaimed.
                }
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly Exception? _throwInstead;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public StubHandler(Exception throwInstead)
        {
            _throwInstead = throwInstead;
            _respond = _ => throw _throwInstead;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throwInstead is not null)
            {
                throw _throwInstead;
            }
            return Task.FromResult(_respond(request));
        }
    }

    /// <summary>Mirrors <see cref="MainWindow.PullCatalogPricesAsync"/>'s exact guard.</summary>
    private static async Task PullAndApplyAsync(CatalogPriceReplicaClient client, BranchSyncStore store, Guid organizationId, string deviceToken)
    {
        var since = store.GetCatalogPricesCursor() ?? DateTimeOffset.MinValue;
        var outcome = await client.PullAsync(since, deviceToken);
        if (!outcome.Success || outcome.Items is null || outcome.RemovedPresentationIds is null || outcome.ServerTimeUtc is null)
        {
            return;
        }

        var replicaItems = outcome.Items
            .Select(row => new CatalogPriceReplicaItem(
                row.PresentationId, organizationId, row.ProductId, row.ProductName, row.PresentationName,
                row.IdentificationCode, row.QuantityBehavior, row.UnitId, row.UnitPrice, row.EffectiveFrom, row.UpdatedAtUtc))
            .ToList();

        store.ApplyCatalogPriceSync(replicaItems, outcome.RemovedPresentationIds, outcome.ServerTimeUtc.Value);
    }

    [Fact]
    public async Task UnreachableHost_LeavesReplicaAndCursorByteIdentical()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;
        store.ApplyCatalogPriceSync(
            [new CatalogPriceReplicaItem(presentationId, organizationId, Guid.NewGuid(), "Flour", "1kg Bag", "CODE1", "Integral", Guid.NewGuid(), 10m, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow)],
            [], serverTime);

        var httpClient = new HttpClient(new StubHandler(new HttpRequestException("connection refused")))
        {
            BaseAddress = new Uri("https://unreachable.invalid"),
        };
        var client = new CatalogPriceReplicaClient(httpClient);

        await PullAndApplyAsync(client, store, organizationId, "device-token");

        var replica = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Equal(presentationId, replica.PresentationId);
        Assert.Equal(10m, replica.UnitPrice);
        Assert.Equal(serverTime, store.GetCatalogPricesCursor());
    }

    [Fact]
    public async Task Unauthorized401_LeavesReplicaAndCursorByteIdentical()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;
        store.ApplyCatalogPriceSync(
            [new CatalogPriceReplicaItem(presentationId, organizationId, Guid.NewGuid(), "Flour", "1kg Bag", "CODE1", "Integral", Guid.NewGuid(), 10m, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow)],
            [], serverTime);

        var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
        {
            BaseAddress = new Uri("https://cloud.invalid"),
        };
        var client = new CatalogPriceReplicaClient(httpClient);

        await PullAndApplyAsync(client, store, organizationId, "revoked-device-token");

        var replica = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Equal(presentationId, replica.PresentationId);
        Assert.Equal(10m, replica.UnitPrice);
        Assert.Equal(serverTime, store.GetCatalogPricesCursor());
    }

    [Fact]
    public async Task Unauthorized401_OnEmptyStore_NeverAdvancesCursorFromNull()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();

        var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
        {
            BaseAddress = new Uri("https://cloud.invalid"),
        };
        var client = new CatalogPriceReplicaClient(httpClient);

        await PullAndApplyAsync(client, store, organizationId, "revoked-device-token");

        Assert.Empty(store.ListCatalogPriceReplica());
        Assert.Null(store.GetCatalogPricesCursor());
    }
}
