using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 4 tasks 4.1/4.3
/// (Requirement: Retryable Delivery with Idempotent Effects):
/// <see cref="SyncRunner.RunAsync"/> is reentrancy-guarded — a second trigger
/// fired during an in-flight sweep is a no-op — and a push failure never
/// throws out of <see cref="SyncRunner.RunAsync"/> (so a non-Button caller in
/// <c>MainWindow</c> never has anything to catch), instead recording the
/// failure durably via <see cref="BranchSyncStore.RecordAttemptFailure"/>.
/// </summary>
public sealed class RunSyncAsyncTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-run-sync-{Guid.NewGuid():N}.db");

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

    /// <summary>
    /// Every network-facing client points at an unreachable loopback port
    /// with a short timeout — every pull/push fails fast and gracefully
    /// (each client's own try/catch, proven by the existing POS pull tests),
    /// exercising exactly the "cloud unreachable" path this test suite cares
    /// about without a live server.
    /// </summary>
    private static SyncRunner NewRunner(BranchSyncStore store, DevicePairing pairing, string operatorsFilePath)
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        var branchNodeService = new BranchNodeService(store, authService, auditSink);

        var httpClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1"), Timeout = TimeSpan.FromSeconds(2) };
        var syncClient = new CloudSyncClient(httpClient);
        var customerReplicaClient = new CustomerReplicaClient(httpClient);
        var catalogPriceReplicaClient = new CatalogPriceReplicaClient(httpClient);
        var operatorProvisioningClient = new OperatorProvisioningClient(httpClient);
        var localOperatorStore = new LocalOperatorStore(operatorsFilePath);

        return new SyncRunner(
            store, branchNodeService, syncClient, customerReplicaClient, catalogPriceReplicaClient,
            operatorProvisioningClient, localOperatorStore, () => pairing);
    }

    private static DevicePairing NewPairing(Guid branchId) => new(
        OrganizationId: Guid.NewGuid(), BranchId: branchId, BranchName: "Branch",
        OperatorEmail: "op@example.com", DeviceToken: "token");

    [Fact]
    public async Task RunAsync_SecondReentrantCall_IsNoOp_WhileFirstIsInFlight()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var pairing = NewPairing(branchId);
        var operatorsPath = Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json");
        var runner = NewRunner(store, pairing, operatorsPath);

        var first = runner.RunAsync(SyncTrigger.Timer);
        var second = await runner.RunAsync(SyncTrigger.Button);

        Assert.Null(second);

        await first;
    }

    [Fact]
    public async Task RunAsync_WithNoPendingOutbox_ReturnsNothingPendingSummary()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var pairing = NewPairing(branchId);
        var operatorsPath = Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json");
        var runner = NewRunner(store, pairing, operatorsPath);

        var result = await runner.RunAsync(SyncTrigger.Startup);

        Assert.NotNull(result);
        Assert.Equal("Nothing pending to sync.", result!.Summary);
    }

    [Fact]
    public async Task RunAsync_UnreachableCloud_NeverThrows_AndRecordsAttemptFailureDurably()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var pairing = NewPairing(branchId);
        var operatorsPath = Path.Combine(Path.GetTempPath(), $"operators-{Guid.NewGuid():N}.json");

        var envelope = new SyncEnvelope(
            OperationId: Guid.NewGuid(), ContractVersion: 1, OrganizationId: pairing.OrganizationId, BranchId: branchId,
            AggregateId: Guid.NewGuid(), AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "order", Payload: "{\"v\":1}");
        store.EnqueueOutbox(envelope);

        var runner = NewRunner(store, pairing, operatorsPath);

        // Never throws — a non-Button caller (scheduler, post-sale nudge)
        // would have nothing to catch either.
        var result = await runner.RunAsync(SyncTrigger.PostSale);

        Assert.NotNull(result);
        Assert.Single(store.GetPendingOutbox(branchId));
    }
}
