using Commerce.BranchNode;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 3 (Requirement: Inbound
/// Materialization Contract): an unknown `payload_kind` writes no `inbox`
/// row and rolls back; a handler throwing (simulated via
/// <see cref="BranchSyncStore.SimulateInterruptedInboundApply"/>) leaves no
/// `inbox` row; an inbound "order" envelope materializes into
/// `inbound_orders` through the shared handler seam, and replaying one
/// `operation_id` materializes exactly one row.
/// </summary>
public sealed class InboundMaterializationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-inbound-materialization-{Guid.NewGuid():N}.db");

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

    private static SyncEnvelope NewEnvelope(string payloadKind, string payload, Guid? operationId = null, Guid? aggregateId = null) => new(
        OperationId: operationId ?? Guid.NewGuid(), ContractVersion: 1, OrganizationId: Guid.NewGuid(), BranchId: Guid.NewGuid(),
        AggregateId: aggregateId ?? Guid.NewGuid(), AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: payloadKind, Payload: payload);

    private static int CountInboxRows(string connectionString, Guid operationId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM inbox WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        return (int)(long)command.ExecuteScalar()!;
    }

    private static int CountInboundOrders(string connectionString, Guid orderId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM inbound_orders WHERE order_id = $id;";
        command.Parameters.AddWithValue("$id", orderId.ToString());
        return (int)(long)command.ExecuteScalar()!;
    }

    [Fact]
    public void ApplyInbound_UnknownKind_WritesNoInboxRow_AndRollsBack()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var envelope = NewEnvelope("unknown-kind", "{\"v\":1}");

        var result = store.ApplyInbound(envelope);

        Assert.Equal(InboundApplyOutcome.UnknownKind, result.Outcome);
        Assert.Equal(0, CountInboxRows(ConnectionString, envelope.OperationId));
    }

    [Fact]
    public void SimulateInterruptedInboundApply_LeavesNoInboxRow()
    {
        var envelope = NewEnvelope("order", SyncPayloadCodec.Serialize(
            new OrderPayloadV1(Guid.NewGuid(), Guid.NewGuid(), "RegisteredCustomer", Lines: [])));

        using (var store = new BranchSyncStore(ConnectionString))
        {
            store.SimulateInterruptedInboundApply(envelope);
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        Assert.Equal(0, CountInboxRows(ConnectionString, envelope.OperationId));
        Assert.Equal(0, CountInboundOrders(ConnectionString, envelope.AggregateId));
    }

    [Fact]
    public void ApplyInbound_OrderEnvelope_MaterializesInboundOrders_ThroughSharedSeam()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var orderId = Guid.NewGuid();
        var envelope = NewEnvelope(
            "order",
            SyncPayloadCodec.Serialize(new OrderPayloadV1(orderId, Guid.NewGuid(), "Guest", Lines: [])),
            operationId: orderId,
            aggregateId: orderId);

        var result = store.ApplyInbound(envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, CountInboxRows(ConnectionString, orderId));
        Assert.Equal(1, CountInboundOrders(ConnectionString, orderId));
    }

    [Fact]
    public void ApplyInbound_ReplayingSameOperationId_MaterializesExactlyOneInboundOrdersRow()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var orderId = Guid.NewGuid();
        var envelope = NewEnvelope(
            "order",
            SyncPayloadCodec.Serialize(new OrderPayloadV1(orderId, Guid.NewGuid(), "Guest", Lines: [])),
            operationId: orderId,
            aggregateId: orderId);

        var first = store.ApplyInbound(envelope);
        var second = store.ApplyInbound(envelope);

        Assert.Equal(InboundApplyOutcome.Applied, first.Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(1, CountInboundOrders(ConnectionString, orderId));
    }

    [Fact]
    public void ApplyInbound_SaleKind_DedupOnly_WritesNoInboundOrdersRow()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var envelope = NewEnvelope("sale", SyncPayloadCodec.Serialize(
            new SalePayloadV1(Guid.NewGuid(), 10m, "Manual", DateTimeOffset.UtcNow, Lines: [])));

        var result = store.ApplyInbound(envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal(1, CountInboxRows(ConnectionString, envelope.OperationId));
        Assert.Equal(0, CountInboundOrders(ConnectionString, envelope.AggregateId));
    }
}
