using Commerce.BranchNode;

namespace Commerce.Integration;

/// <summary>
/// purchases-receptions-and-stock T5, branch-node side: the `stock` cursor/replica channel in the local SQLite store.
/// `ApplyStockSync` upserts ABSOLUTE on-hand snapshots and advances the REUSED `sync_cursors` table in ONE transaction;
/// an interrupted apply leaves both byte-identical across a restart. Mirrors <see cref="CatalogPriceReplicaTests"/>.
/// </summary>
public sealed class StockReplicaStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-stock-{Guid.NewGuid():N}.db");

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

    [Fact]
    public void StockCursor_IsNullUntilTheFirstSuccessfulApply_AndNothingIsKnown()
    {
        using var store = new BranchSyncStore(ConnectionString);

        Assert.Null(store.GetStockCursor());
        Assert.Empty(store.GetStockOnHand([Guid.NewGuid()]));
    }

    [Fact]
    public void ApplyStockSync_StoresTheSnapshots_AndAdvancesTheCursor()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var (meat, sausage, untouched) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var serverTime = DateTimeOffset.UtcNow;

        store.ApplyStockSync([new StockReplicaItem(meat, 117.5m), new StockReplicaItem(sausage, -3m)], serverTime);

        var known = store.GetStockOnHand([meat, sausage, untouched]);
        Assert.Equal(117.5m, known[meat]);
        Assert.Equal(-3m, known[sausage]); // negative stock is replicated as is
        Assert.False(known.ContainsKey(untouched)); // never moved: unknown, not zero
        Assert.Equal(serverTime, store.GetStockCursor());
    }

    [Fact]
    public void ApplyStockSync_IsIdempotent_AnAbsoluteSnapshotReplacesThePreviousOne()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var meat = Guid.NewGuid();

        store.ApplyStockSync([new StockReplicaItem(meat, 120m)], DateTimeOffset.UtcNow);
        store.ApplyStockSync([new StockReplicaItem(meat, 117.5m)], DateTimeOffset.UtcNow);
        store.ApplyStockSync([new StockReplicaItem(meat, 117.5m)], DateTimeOffset.UtcNow);

        Assert.Equal(117.5m, store.GetStockOnHand([meat])[meat]);
    }

    [Fact]
    public void AnEmptyApply_StillAdvancesTheCursor_SoTheReplicaIsLabelledFresh()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var first = DateTimeOffset.UtcNow.AddMinutes(-10);
        var second = DateTimeOffset.UtcNow;

        store.ApplyStockSync([new StockReplicaItem(Guid.NewGuid(), 1m)], first);
        store.ApplyStockSync([], second);

        Assert.Equal(second, store.GetStockCursor());
    }

    [Fact]
    public void InterruptedStockSync_NeverPersistsPartialReplicaOrCursorAcrossRestart()
    {
        var meat = Guid.NewGuid();
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedStockSync([new StockReplicaItem(meat, 5m)], DateTimeOffset.UtcNow);
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Null(reopened.GetStockCursor());
        Assert.Empty(reopened.GetStockOnHand([meat]));
    }

    [Fact]
    public void AStaleReplica_RemainsReadable_WithItsOldCursor()
    {
        var meat = Guid.NewGuid();
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.ApplyStockSync([new StockReplicaItem(meat, 40m)], old);
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Equal(40m, reopened.GetStockOnHand([meat])[meat]);
        Assert.Equal(old, reopened.GetStockCursor());
    }
}
