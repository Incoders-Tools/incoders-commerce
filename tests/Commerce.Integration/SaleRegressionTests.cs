using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 2 task 2.3/2.9 (Requirement:
/// Generic Outbox Payload Contract, scenario "Existing sale enqueue is
/// unaffected"): <see cref="BranchNodeService.CompleteOfflineSale"/>/
/// <see cref="BranchNodeService.CompleteScannedSale"/> enqueue "sale" through
/// the generic `sync_outbox` path with unchanged observable sync behavior and
/// no dependency on cloud reachability for the sale itself — the regression
/// anchor for the whole Phase F generalization.
/// </summary>
public sealed class SaleRegressionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-regression-{Guid.NewGuid():N}.db");

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

    private static BranchNodeService NewService(BranchSyncStore store)
    {
        var auditSink = new InMemoryAuditSink();
        var authService = new TenantAuthorizationService(auditSink);
        return new BranchNodeService(store, authService, auditSink);
    }

    [Fact]
    public void CompleteOfflineSale_EnqueuesThroughGenericPath_WithNoSaleEffectColumnOnLegacyOutbox()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();

        var result = service.CompleteOfflineSale(
            organizationId: Guid.NewGuid(), branchId: branchId, actorId: Guid.NewGuid(),
            saleId: Guid.NewGuid(), totalAmount: 25m, operationId: Guid.NewGuid(), correlationId: Guid.NewGuid());

        Assert.True(result.WasNewlyCommitted);

        var pending = store.GetPendingOutbox(branchId);
        var entry = Assert.Single(pending);
        Assert.Equal("sale", entry.PayloadKind);

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(entry.Payload);
        Assert.Equal(result.Effect.SaleId, payload.SaleId);
        Assert.Equal(25m, payload.TotalAmount);
        Assert.Equal("Manual", payload.SaleKind);
    }

    [Fact]
    public void CompleteScannedSale_EnqueuesThroughGenericPath_WithLinesInPayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var lines = new[] { new SaleLine(saleId, 1, Guid.NewGuid(), "code", "Product", "Presentation", 1m, 10m, 10m) };

        var result = service.CompleteScannedSale(
            organizationId: Guid.NewGuid(), branchId: branchId, actorId: Guid.NewGuid(),
            saleId: saleId, lines: lines, totalAmount: 10m, operationId: Guid.NewGuid(), correlationId: Guid.NewGuid());

        Assert.True(result.WasNewlyCommitted);

        var pending = store.GetPendingOutbox(branchId);
        var entry = Assert.Single(pending);
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(entry.Payload);
        Assert.Equal("Scanned", payload.SaleKind);
        Assert.Single(payload.Lines);
    }

    /// <summary>
    /// ADR-002: no cloud reachability dependency — the whole test never
    /// constructs a <c>CloudSyncClient</c> or touches the network, yet the
    /// sale commits and returns immediately.
    /// </summary>
    [Fact]
    public void CompleteOfflineSale_NeverRequiresCloudReachability()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);

        var result = service.CompleteOfflineSale(
            organizationId: Guid.NewGuid(), branchId: Guid.NewGuid(), actorId: Guid.NewGuid(),
            saleId: Guid.NewGuid(), totalAmount: 5m, operationId: Guid.NewGuid(), correlationId: Guid.NewGuid());

        Assert.True(result.WasNewlyCommitted);
    }

    [Fact]
    public void DuplicateSaleCommit_ThroughGenericPath_AppliesNoSecondEffect()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var first = service.CompleteOfflineSale(Guid.NewGuid(), branchId, Guid.NewGuid(), saleId, 8m, operationId, Guid.NewGuid());
        var second = service.CompleteOfflineSale(Guid.NewGuid(), branchId, Guid.NewGuid(), saleId, 8m, operationId, Guid.NewGuid());

        Assert.True(first.WasNewlyCommitted);
        Assert.False(second.WasNewlyCommitted);
        Assert.Single(store.GetPendingOutbox(branchId));
    }
}
