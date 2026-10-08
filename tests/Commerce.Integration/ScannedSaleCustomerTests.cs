using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// Desktop POS redesign T5: the customer selected in the sale-time picker is
/// recorded on scanned sales — on the local `sale_effects` row and in the
/// outbox `SalePayloadV1` — and stays null for walk-in sales.
/// </summary>
public sealed class ScannedSaleCustomerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-scanned-customer-{Guid.NewGuid():N}.db");

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

    private BranchNodeService NewService(BranchSyncStore store)
    {
        var auditSink = new Commerce.Application.Audit.InMemoryAuditSink();
        return new BranchNodeService(store, new Commerce.Application.Access.TenantAuthorizationService(auditSink), auditSink).WithOpenSession();
    }

    private static IReadOnlyList<SaleLine> Lines(Guid saleId) =>
        [new SaleLine(saleId, 1, Guid.NewGuid(), "7791234567890", "Flour", "1kg Bag", 1m, 50m, 50m)];

    private static BranchOutboxCommitResult Complete(
        BranchNodeService service, Guid branchId, Guid saleId, Guid? customerId, Guid? operationId = null) =>
        service.CompleteScannedSale(
            organizationId: Guid.NewGuid(),
            branchId: branchId,
            actorId: Guid.NewGuid(),
            saleId: saleId,
            lines: Lines(saleId),
            totalAmount: 50m,
            operationId: operationId ?? Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            customerId: customerId);

    private static Guid? StoredCustomerId(string connectionString, Guid saleId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT customer_id FROM sale_effects WHERE sale_id = $saleId;";
        command.Parameters.AddWithValue("$saleId", saleId.ToString());
        var raw = command.ExecuteScalar();
        return raw is null or DBNull ? null : Guid.Parse((string)raw);
    }

    [Fact]
    public void CompleteScannedSale_WithCustomer_RecordsItOnTheSaleEffectAndTheOutboxPayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var result = Complete(service, branchId, saleId, customerId);

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal(customerId, result.Effect.CustomerId);
        Assert.Equal(customerId, StoredCustomerId(ConnectionString, saleId));

        var envelope = Assert.Single(store.GetPendingOutbox(branchId));
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload);
        Assert.Equal(customerId, payload.CustomerId);
    }

    [Fact]
    public void CompleteScannedSale_WalkIn_LeavesTheCustomerNull()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        var result = Complete(service, branchId, saleId, customerId: null);

        Assert.Null(result.Effect.CustomerId);
        Assert.Null(StoredCustomerId(ConnectionString, saleId));
        var envelope = Assert.Single(store.GetPendingOutbox(branchId));
        Assert.Null(SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload).CustomerId);
    }

    [Fact]
    public void CompleteScannedSale_IdempotentReplay_KeepsTheRecordedCustomer()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        Complete(service, branchId, saleId, customerId, operationId);
        var replay = Complete(service, branchId, saleId, Guid.NewGuid(), operationId);

        Assert.False(replay.WasNewlyCommitted);
        Assert.Equal(customerId, replay.Effect.CustomerId);
        Assert.Equal(customerId, StoredCustomerId(ConnectionString, saleId));
    }

    [Fact]
    public void BranchDb_CreatedBeforeTheCustomerColumn_IsMigratedAndStillCommitsSales()
    {
        using (var legacy = new SqliteConnection(ConnectionString))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE sale_effects (
                    sale_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, total_amount TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL, sale_kind TEXT NOT NULL DEFAULT 'Manual');
                INSERT INTO sale_effects VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '10', '2026-01-01T00:00:00.0000000+00:00', 'Manual');
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new BranchSyncStore(ConnectionString);
        using var reopened = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var saleId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var result = Complete(service, Guid.NewGuid(), saleId, customerId);

        Assert.True(result.WasNewlyCommitted);
        Assert.Equal(customerId, StoredCustomerId(ConnectionString, saleId));
        Assert.Null(StoredCustomerId(ConnectionString, Guid.Parse("11111111-1111-1111-1111-111111111111")));
    }
}
