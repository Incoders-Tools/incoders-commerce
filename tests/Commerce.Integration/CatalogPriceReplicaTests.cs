using Commerce.BranchNode;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 6 task 6.2 (design.md "BranchNode
/// replication: one channel, not two"): `ApplyCatalogPriceSync` upserts
/// `catalog_replica`/`price_replica` and advances the REUSED `sync_cursors`
/// table (`catalog-prices` channel) in one transaction, and
/// `SimulateInterruptedCatalogPriceSync` proves a failed/interrupted apply
/// leaves both byte-identical across a restart. Mirrors
/// <see cref="CustomerReplicaTests"/> exactly. Deviation note: placed in
/// `tests/Commerce.Integration`, the same path Unit 6's customer-identity
/// precedent used, because `tests/Commerce.BranchNode` does not exist as a
/// project in this solution.
/// </summary>
public sealed class CatalogPriceReplicaTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-catalog-prices-{Guid.NewGuid():N}.db");

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

    private static CatalogPriceReplicaItem SampleItem(
        Guid presentationId,
        Guid organizationId,
        decimal? unitPrice = 100m,
        string presentationName = "1kg Bag",
        DateTimeOffset? updatedAtUtc = null) => new(
            PresentationId: presentationId,
            OrganizationId: organizationId,
            ProductId: Guid.NewGuid(),
            ProductName: "Flour",
            PresentationName: presentationName,
            IdentificationCode: "7791234567890",
            QuantityBehavior: "Integral",
            UnitId: Guid.NewGuid(),
            UnitPrice: unitPrice,
            EffectiveFrom: unitPrice is null ? null : DateOnly.FromDateTime(DateTime.UtcNow),
            UpdatedAtUtc: updatedAtUtc ?? DateTimeOffset.UtcNow);

    [Fact]
    public void CatalogPricesCursor_IsNullUntilFirstSuccessfulApply()
    {
        using var store = new BranchSyncStore(ConnectionString);

        Assert.Null(store.GetCatalogPricesCursor());
    }

    [Fact]
    public void ApplyCatalogPriceSync_UpsertsCatalogAndPrice_AndAdvancesCursor()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;

        store.ApplyCatalogPriceSync(
            items: [SampleItem(presentationId, organizationId)],
            removedPresentationIds: [],
            serverTimeUtc: serverTime);

        var replica = store.ListCatalogPriceReplica();
        var entry = Assert.Single(replica);
        Assert.Equal(presentationId, entry.PresentationId);
        Assert.Equal(100m, entry.UnitPrice);
        Assert.Equal(serverTime, store.GetCatalogPricesCursor());
    }

    [Fact]
    public void ApplyCatalogPriceSync_IsIdempotent_ReplacesRatherThanDuplicates()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();

        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, presentationName: "Original")], [], DateTimeOffset.UtcNow);
        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, presentationName: "Renamed")], [], DateTimeOffset.UtcNow);

        var entry = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Equal("Renamed", entry.PresentationName);
    }

    [Fact]
    public void ApplyCatalogPriceSync_NoEffectivePrice_NeverSubstitutesZero_RemovesPriceRow()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();

        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, unitPrice: 50m)], [], DateTimeOffset.UtcNow);
        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId, unitPrice: null)], [], DateTimeOffset.UtcNow);

        var entry = Assert.Single(store.ListCatalogPriceReplica());
        Assert.Null(entry.UnitPrice);
    }

    [Fact]
    public void ApplyCatalogPriceSync_RemovedPresentationIds_DeletesCatalogAndPriceRows()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        store.ApplyCatalogPriceSync([SampleItem(presentationId, organizationId)], [], DateTimeOffset.UtcNow);

        store.ApplyCatalogPriceSync([], [presentationId], DateTimeOffset.UtcNow);

        Assert.Empty(store.ListCatalogPriceReplica());
    }

    [Fact]
    public void InterruptedCatalogPriceSync_NeverPersistsPartialReplicaOrCursorAcrossRestart()
    {
        var organizationId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var serverTime = DateTimeOffset.UtcNow;

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedCatalogPriceSync(
                items: [SampleItem(presentationId, organizationId)],
                removedPresentationIds: [],
                serverTimeUtc: serverTime);
        }

        // Branch restarts: reopen the same SQLite file as a fresh store.
        using var restarted = new BranchSyncStore(ConnectionString);
        Assert.Empty(restarted.ListCatalogPriceReplica());
        Assert.Null(restarted.GetCatalogPricesCursor());
    }
}
