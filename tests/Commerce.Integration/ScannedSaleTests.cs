using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 7 task 7.1/7.3 (RED+GREEN): a
/// scanned sale writes `sale_effects.sale_kind = 'Scanned'` plus real
/// `sale_lines` rows, while the existing manual-total path writes
/// `sale_kind = 'Manual'` with zero lines — same commit path
/// (<see cref="BranchSyncStore"/>'s atomic sale+outbox transaction),
/// distinguishable only by kind and by whether lines exist (design.md "POS:
/// two explicit buttons, not a mode toggle").
/// </summary>
public sealed class ScannedSaleTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-scanned-sale-{Guid.NewGuid():N}.db");

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

    private static SyncEnvelope SaleEnvelope(Guid saleId, Guid organizationId, Guid branchId, Guid actorId) => new(
        OperationId: Guid.NewGuid(),
        ContractVersion: 1,
        OrganizationId: organizationId,
        BranchId: branchId,
        AggregateId: saleId,
        AggregateVersion: 1,
        ActorId: actorId,
        CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow,
        PayloadKind: "sale",
        Payload: "{\"v\":1}");

    [Fact]
    public void CommitScannedSaleAtomically_WritesScannedKind_AndRealSaleLines()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();

        var lines = new[]
        {
            new SaleLine(saleId, 1, presentationId, "7791234567890", "Flour", "1kg Bag", 2m, 100m, 200m),
        };
        var effect = new SaleEffect(saleId, branchId, 200m, DateTimeOffset.UtcNow);

        var result = store.CommitScannedSaleAtomically(SaleEnvelope(saleId, organizationId, branchId, actorId), effect, lines);

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal("Scanned", result.Effect.SaleKind);

        var storedLines = store.ListSaleLines(saleId);
        var storedLine = Assert.Single(storedLines);
        Assert.Equal(presentationId, storedLine.PresentationId);
        Assert.Equal(200m, storedLine.LineTotal);
    }

    [Fact]
    public void CommitSaleAtomically_ManualPath_WritesManualKind_AndZeroSaleLines()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        var result = store.CommitSaleAtomically(
            SaleEnvelope(saleId, organizationId, branchId, actorId),
            new SaleEffect(saleId, branchId, 19.99m, DateTimeOffset.UtcNow));

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal("Manual", result.Effect.SaleKind);
        Assert.Empty(store.ListSaleLines(saleId));
    }

    [Fact]
    public void BranchNodeService_CompleteScannedSale_PersistsScannedKindAndLines()
    {
        var auditSink = new Commerce.Application.Audit.InMemoryAuditSink();
        var authService = new Commerce.Application.Access.TenantAuthorizationService(auditSink);
        using var store = new BranchSyncStore(ConnectionString);
        var service = new BranchNodeService(store, authService, auditSink);

        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();

        var result = service.CompleteScannedSale(
            organizationId: organizationId,
            branchId: branchId,
            actorId: actorId,
            saleId: saleId,
            lines:
            [
                new SaleLine(saleId, 1, presentationId, "7791234567890", "Flour", "1kg Bag", 1m, 50m, 50m),
            ],
            totalAmount: 50m,
            operationId: Guid.NewGuid(),
            correlationId: Guid.NewGuid());

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal("Scanned", result.Effect.SaleKind);
        Assert.Single(store.ListSaleLines(saleId));
    }
}
