using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Discounts Recorded and Synchronized With Their
/// Authorization", branch side: the local rows and the queued payload carry the
/// line and sale discounts plus the authorization marker; a sale without
/// discounts carries none; committing stays offline and idempotent.
/// </summary>
public sealed class SaleDiscountCommitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-discount-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _branchId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();

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
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink).WithOpenSession();
    }

    private static SaleLine Line(Guid saleId, int number, decimal quantity, decimal unit, decimal? percent = null, decimal? amount = null) =>
        new(saleId, number, Guid.NewGuid(), "code" + number, "Product " + number, "1u", quantity, unit, quantity * unit, percent, amount);

    private BranchOutboxCommitResult Commit(
        BranchNodeService service, Guid saleId, IReadOnlyList<SaleLine> lines, decimal total,
        SaleDiscount? saleDiscount = null, DiscountAuthorization? authorization = null, Guid? operationId = null) =>
        service.CompleteScannedSale(
            _organizationId, _branchId, _operatorId, saleId, lines, total, operationId ?? Guid.NewGuid(), Guid.NewGuid(),
            customerId: null, saleDiscount: saleDiscount, discountAuthorization: authorization);

    [Fact]
    public void DiscountedSale_PersistsLineAndSaleDiscountsAndTheAuthorizationLocally()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        var auth = new DiscountAuthorization(DiscountAuthorization.BranchPin, _operatorId, 4);
        var lines = new[] { Line(saleId, 1, 1m, 1000m, 10m, 100m), Line(saleId, 2, 1m, 500m) };

        Commit(NewService(store), saleId, lines, total: 1330m, new SaleDiscount(5m, 70m), auth);

        var stored = store.ListSaleLines(saleId);
        Assert.Equal(10m, stored[0].LineDiscountPercent);
        Assert.Equal(100m, stored[0].LineDiscountAmount);
        Assert.Equal(1000m, stored[0].LineTotal);
        Assert.Null(stored[1].LineDiscountPercent);
        Assert.Null(stored[1].LineDiscountAmount);

        var effect = store.GetSaleEffect(saleId)!;
        Assert.Equal(1330m, effect.TotalAmount);
        Assert.Equal(5m, effect.SaleDiscountPercent);
        Assert.Equal(70m, effect.SaleDiscountAmount);
        Assert.Equal(auth, effect.DiscountAuthorization);
    }

    [Fact]
    public void DiscountedSale_QueuesAPayloadWithTheSameDataAndTheOperator()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        var auth = new DiscountAuthorization(DiscountAuthorization.BranchPin, _operatorId, 4);

        Commit(NewService(store), saleId, [Line(saleId, 1, 2m, 100m, 10m, 20m)], 162m, new SaleDiscount(10m, 18m), auth);

        var envelope = Assert.Single(store.GetPendingOutbox(_branchId));
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(envelope.Payload);
        Assert.Equal(162m, payload.TotalAmount);
        Assert.Equal(10m, payload.Lines[0].LineDiscountPercent);
        Assert.Equal(20m, payload.Lines[0].LineDiscountAmount);
        Assert.Equal(10m, payload.SaleDiscountPercent);
        Assert.Equal(18m, payload.SaleDiscountAmount);
        Assert.Equal(new DiscountAuthorization("branch-pin", _operatorId, 4), payload.DiscountAuthorization);
    }

    [Fact]
    public void SaleWithoutDiscounts_CarriesNoDiscountData()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();

        Commit(NewService(store), saleId, [Line(saleId, 1, 1m, 100m)], 100m);

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload);
        Assert.Null(payload.SaleDiscountPercent);
        Assert.Null(payload.SaleDiscountAmount);
        Assert.Null(payload.DiscountAuthorization);
        Assert.Null(payload.Lines[0].LineDiscountPercent);
        var effect = store.GetSaleEffect(saleId)!;
        Assert.Null(effect.DiscountAuthorization);
        Assert.Null(effect.SaleDiscountAmount);
    }

    [Fact]
    public void Replaying_TheSameOperation_ReturnsTheStoredDiscountedEffect_WithoutASecondSale()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var auth = new DiscountAuthorization(DiscountAuthorization.BranchPin, _operatorId, 4);
        var lines = new[] { Line(saleId, 1, 1m, 100m, 10m, 10m) };

        var first = Commit(service, saleId, lines, 81m, new SaleDiscount(10m, 9m), auth, operationId);
        var replay = Commit(service, saleId, lines, 81m, new SaleDiscount(10m, 9m), auth, operationId);

        Assert.True(first.WasNewlyCommitted);
        Assert.False(replay.WasNewlyCommitted);
        Assert.Equal(9m, replay.Effect.SaleDiscountAmount);
        Assert.Equal(auth, replay.Effect.DiscountAuthorization);
        Assert.Single(store.GetPendingOutbox(_branchId));
    }

    [Fact]
    public void PayloadFromBeforeDiscounts_StillDeserializes()
    {
        const string legacy =
            """{"saleId":"11111111-1111-1111-1111-111111111111","totalAmount":10,"saleKind":"Scanned","occurredAtUtc":"2026-01-01T00:00:00+00:00","lines":[{"saleId":"11111111-1111-1111-1111-111111111111","lineNumber":1,"presentationId":"22222222-2222-2222-2222-222222222222","identificationCode":null,"productName":"P","presentationName":"1u","quantity":1,"unitPrice":10,"lineTotal":10}]}""";

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(legacy);

        Assert.Equal(10m, payload.TotalAmount);
        Assert.Null(payload.SaleDiscountPercent);
        Assert.Null(payload.DiscountAuthorization);
        Assert.Null(payload.Lines[0].LineDiscountAmount);
    }
}
