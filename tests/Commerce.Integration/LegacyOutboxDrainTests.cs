using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 2 (legacy drain): a
/// pre-seeded LEGACY `outbox` row (the sale-shaped schema, written directly
/// via raw SQL to simulate a pre-upgrade `branch.db`) is returned by
/// <see cref="BranchSyncStore.GetPendingOutbox"/> with a reconstituted
/// <see cref="SalePayloadV1"/>, acknowledges correctly, and the `outbox` DDL
/// is byte-identical afterward.
/// </summary>
public sealed class LegacyOutboxDrainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-legacy-drain-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Seeds a row directly into the LEGACY `outbox`/`sale_effects` tables —
    /// bypassing every Phase F code path — to simulate a row written by a
    /// pre-upgrade executable.
    /// </summary>
    private static void SeedLegacyRow(string connectionString, Guid branchId, Guid operationId, Guid saleId, decimal totalAmount, string saleKind)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var insertSale = connection.CreateCommand())
        {
            insertSale.CommandText = """
                INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc, sale_kind)
                VALUES ($saleId, $branchId, $totalAmount, $occurredAt, $saleKind);
                """;
            insertSale.Parameters.AddWithValue("$saleId", saleId.ToString());
            insertSale.Parameters.AddWithValue("$branchId", branchId.ToString());
            insertSale.Parameters.AddWithValue("$totalAmount", totalAmount.ToString());
            insertSale.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            insertSale.Parameters.AddWithValue("$saleKind", saleKind);
            insertSale.ExecuteNonQuery();
        }

        using (var insertOutbox = connection.CreateCommand())
        {
            insertOutbox.CommandText = """
                INSERT INTO outbox (
                    operation_id, branch_id, organization_id, aggregate_id, aggregate_version,
                    actor_id, correlation_id, occurred_at_utc, payload_kind, payload,
                    sale_id, total_amount, status, acknowledged_at_utc)
                VALUES (
                    $operationId, $branchId, $organizationId, $aggregateId, 1,
                    $actorId, $correlationId, $occurredAt, 'sale', '{}',
                    $saleId, $totalAmount, 'Pending', NULL);
                """;
            insertOutbox.Parameters.AddWithValue("$operationId", operationId.ToString());
            insertOutbox.Parameters.AddWithValue("$branchId", branchId.ToString());
            insertOutbox.Parameters.AddWithValue("$organizationId", Guid.NewGuid().ToString());
            insertOutbox.Parameters.AddWithValue("$aggregateId", saleId.ToString());
            insertOutbox.Parameters.AddWithValue("$actorId", Guid.NewGuid().ToString());
            insertOutbox.Parameters.AddWithValue("$correlationId", Guid.NewGuid().ToString());
            insertOutbox.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            insertOutbox.Parameters.AddWithValue("$saleId", saleId.ToString());
            insertOutbox.Parameters.AddWithValue("$totalAmount", totalAmount.ToString());
            insertOutbox.ExecuteNonQuery();
        }
    }

    private static List<(string Name, string Type, int NotNull, int Pk)> TableInfo(string connectionString, string table)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var results = new List<(string, string, int, int)>();
        while (reader.Read())
        {
            results.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(5)));
        }
        return results;
    }

    [Fact]
    public void GetPendingOutbox_ReturnsLegacyRow_WithReconstitutedSalePayloadV1()
    {
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        // Open once via BranchSyncStore so the sync_outbox/inbound_orders DDL
        // exists, then seed the legacy row directly.
        using (var bootstrap = new BranchSyncStore(ConnectionString))
        {
        }
        SeedLegacyRow(ConnectionString, branchId, operationId, saleId, 42m, "Manual");

        using var store = new BranchSyncStore(ConnectionString);
        var pending = store.GetPendingOutbox(branchId);

        var entry = Assert.Single(pending);
        Assert.Equal(operationId, entry.OperationId);
        Assert.Equal("sale", entry.PayloadKind);

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(entry.Payload);
        Assert.Equal(saleId, payload.SaleId);
        Assert.Equal(42m, payload.TotalAmount);
        Assert.Equal("Manual", payload.SaleKind);
    }

    [Fact]
    public void Acknowledge_LegacyRow_MarksItAcknowledged_AndOutboxDdlStaysByteIdentical()
    {
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        using (var bootstrap = new BranchSyncStore(ConnectionString))
        {
        }

        var ddlBefore = TableInfo(ConnectionString, "outbox");
        SeedLegacyRow(ConnectionString, branchId, operationId, saleId, 15m, "Manual");

        using (var store = new BranchSyncStore(ConnectionString))
        {
            var acknowledged = store.Acknowledge(operationId);
            Assert.True(acknowledged);
            Assert.Empty(store.GetPendingOutbox(branchId));
        }

        var ddlAfter = TableInfo(ConnectionString, "outbox");
        Assert.Equal(ddlBefore, ddlAfter);
    }
}
