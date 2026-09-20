using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 2 (generic outbox):
/// a payload kind other than "sale" enqueues into `sync_outbox` with no
/// `SaleEffect` and no sale-specific column; a failed push leaves
/// `attempt_count`/`last_error` durably on disk after reopening the file.
/// </summary>
public sealed class GenericOutboxTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-generic-outbox-{Guid.NewGuid():N}.db");

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

    private static SyncEnvelope NewOrderEnvelope(Guid branchId, Guid operationId) => new(
        OperationId: operationId, ContractVersion: 1, OrganizationId: Guid.NewGuid(), BranchId: branchId,
        AggregateId: operationId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "order",
        Payload: SyncPayloadCodec.Serialize(new OrderPayloadV1(operationId, branchId, "RegisteredCustomer", Lines: [])));

    [Fact]
    public void EnqueueOutbox_NonSaleKind_RequiresNoSaleEffect_AndNoSaleSpecificColumn()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        store.EnqueueOutbox(NewOrderEnvelope(branchId, operationId));

        var pending = store.GetPendingOutbox(branchId);
        var entry = Assert.Single(pending);
        Assert.Equal("order", entry.PayloadKind);
        Assert.Equal(operationId, entry.OperationId);

        var payload = SyncPayloadCodec.Deserialize<OrderPayloadV1>(entry.Payload);
        Assert.Equal(operationId, payload.OrderId);
    }

    [Fact]
    public void RecordAttemptFailure_LeavesAttemptCountAndError_DurablyOnDisk_AfterReopen()
    {
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.EnqueueOutbox(NewOrderEnvelope(branchId, operationId));
            store.RecordAttemptFailure(operationId, "cloud unreachable");
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        var pending = reopened.GetPendingOutbox(branchId);
        // Row stays Pending — never dead-lettered.
        Assert.Single(pending);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_count, last_error FROM sync_outbox WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("cloud unreachable", reader.GetString(1));
    }

    [Fact]
    public void Acknowledge_SyncOutboxRow_RemovesItFromPending()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var branchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        store.EnqueueOutbox(NewOrderEnvelope(branchId, operationId));

        var acknowledged = store.Acknowledge(operationId);

        Assert.True(acknowledged);
        Assert.Empty(store.GetPendingOutbox(branchId));
    }
}
