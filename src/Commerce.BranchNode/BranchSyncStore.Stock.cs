using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>
/// One row of the cloud -> branch stock replica: the ABSOLUTE derived on-hand of a presentation as the cloud last
/// published it (may be negative). A presentation that never moved has no row: unknown, not zero.
/// </summary>
public sealed record StockReplicaItem(Guid PresentationId, decimal OnHand);

/// <summary>
/// The `stock` cursor/replica channel of the branch database (purchases-receptions-and-stock T5). Stock is
/// cloud-authoritative, so this is a READ replica: it lets the POS show the availability it last knew, offline included,
/// labelled with the cursor time. It never decrements by itself and never blocks a sale. The replica is for the one
/// branch this node belongs to (the device credential fixes it on the cloud side), so rows carry no organization or
/// branch. Additive and idempotent so a `branch.db` from before stock opens and upgrades.
/// </summary>
public sealed partial class BranchSyncStore
{
    /// <summary>The `sync_cursors.channel` of the stock replica (the same table as customers and catalog-prices).</summary>
    public const string StockChannel = "stock";

    private void EnsureStockReplicaExists()
    {
        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS stock_replica (
                presentation_id TEXT PRIMARY KEY,
                on_hand TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// The real pull entry point: upserts the absolute snapshots and advances the cursor in ONE transaction, so a failure
    /// never leaves the replica ahead of the cursor or vice versa. An empty list still advances the cursor (the replica
    /// is current as of that time).
    /// </summary>
    public void ApplyStockSync(IReadOnlyList<StockReplicaItem> items, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            WriteStockSync(items, serverTimeUtc, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Test-only atomicity proof: the same writes as <see cref="ApplyStockSync"/> but never committed.</summary>
    public void SimulateInterruptedStockSync(IReadOnlyList<StockReplicaItem> items, DateTimeOffset serverTimeUtc)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            WriteStockSync(items, serverTimeUtc, transaction);
        }
    }

    private void WriteStockSync(IReadOnlyList<StockReplicaItem> items, DateTimeOffset serverTimeUtc, SqliteTransaction transaction)
    {
        foreach (var item in items)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO stock_replica (presentation_id, on_hand) VALUES ($id, $onHand)
                ON CONFLICT(presentation_id) DO UPDATE SET on_hand = excluded.on_hand;
                """;
            command.Parameters.AddWithValue("$id", item.PresentationId.ToString());
            command.Parameters.AddWithValue("$onHand", item.OnHand.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        UpsertCursor(StockChannel, serverTimeUtc, transaction);
    }

    /// <summary>When the stock replica was last refreshed from the cloud; null before the first successful pull.</summary>
    public DateTimeOffset? GetStockCursor()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", StockChannel);

        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : DateTimeOffset.Parse((string)raw, CultureInfo.InvariantCulture);
    }

    /// <summary>The known on-hand of each requested presentation; a presentation with no row is absent from the result.</summary>
    public IReadOnlyDictionary<Guid, decimal> GetStockOnHand(IEnumerable<Guid> presentationIds)
    {
        var result = new Dictionary<Guid, decimal>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT on_hand FROM stock_replica WHERE presentation_id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in presentationIds.Distinct())
        {
            parameter.Value = id.ToString();
            if (command.ExecuteScalar() is string raw)
            {
                result[id] = decimal.Parse(raw, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }
}
