using Commerce.BranchNode;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Ordering;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 3 task 3.8/3.9:
/// <see cref="CloudOrderStore.AttemptDelivery"/> builds a real
/// <see cref="OrderPayloadV1"/> instead of the decorative `"{}"`, delivered
/// through the shared <see cref="BranchSyncStore.ApplyInbound"/> seam.
/// </summary>
public sealed class CloudOrderStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-cloud-order-store-{Guid.NewGuid():N}.db");

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

    private static OrderLineSnapshot NewLine() => new(
        ProductId: Guid.NewGuid(), ProductName: "Product", PresentationId: Guid.NewGuid(), PresentationName: "Presentation",
        QuantityBehavior: QuantityBehavior.FixedQuantity, UnitId: Guid.NewGuid(), Quantity: 2m,
        UnitListPrice: 10m, AppliedDiscountPercentage: 0m, UnitNetPrice: 10m, LineTotal: 20m);

    [Fact]
    public void Submit_WithReachableDestination_DeliversRealOrderPayloadV1_NotDecorativePlaceholder()
    {
        using var branchStore = new BranchSyncStore(ConnectionString);
        var cloudOrderStore = new CloudOrderStore(() => DateTimeOffset.UtcNow);
        var branchId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var scope = new CloudTenantScope(organizationId);
        var orderId = Guid.NewGuid();

        cloudOrderStore.Submit(
            scope, orderId, Guid.NewGuid(), branchId, Guid.NewGuid(),
            new[] { NewLine() }, Guid.NewGuid(), branchStore, hasAvailableStock: true);

        var order = cloudOrderStore.Find(scope, orderId);
        Assert.NotNull(order);
        Assert.Equal(OrderDeliveryStatus.DestinationConfirmed, order!.Status);

        // The branch's own inbox proves a real, decodable OrderPayloadV1 was
        // delivered end-to-end — not the decorative "{}".
        var duplicate = branchStore.ApplyInbound(new SyncEnvelope(
            OperationId: orderId, ContractVersion: 1, OrganizationId: organizationId, BranchId: branchId,
            AggregateId: orderId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "order",
            Payload: SyncPayloadCodec.Serialize(new OrderPayloadV1(orderId, branchId, "RegisteredCustomer", Lines: []))));
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, duplicate.Outcome);
    }

    [Fact]
    public void Submit_WithNoDestination_StaysPending_NeverConfirmed()
    {
        var cloudOrderStore = new CloudOrderStore(() => DateTimeOffset.UtcNow);
        var scope = new CloudTenantScope(Guid.NewGuid());
        var orderId = Guid.NewGuid();

        cloudOrderStore.Submit(
            scope, orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new[] { NewLine() }, Guid.NewGuid(), destination: null, hasAvailableStock: true);

        var order = cloudOrderStore.Find(scope, orderId);
        Assert.Equal(OrderDeliveryStatus.PendingDestination, order!.Status);
        Assert.Equal(OrderPendingReason.DestinationOffline, order.PendingReason);
    }
}
