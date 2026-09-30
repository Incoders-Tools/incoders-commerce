using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Tender Recorded and Synchronized With the Sale", branch
/// side: the local sale record and the queued payload carry the tender for both
/// scan-composed and manual sales; a sale without a tender (and a database from
/// before tenders) still reads; committing is offline and idempotent.
/// </summary>
public sealed class SaleTenderCommitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-tender-{Guid.NewGuid():N}.db");
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

    private static BranchNodeService NewService(BranchSyncStore store)
    {
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink);
    }

    private static SaleLine Line(Guid saleId) =>
        new(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 855m, 855m);

    private BranchOutboxCommitResult CommitScanned(
        BranchNodeService service, Guid saleId, SaleTender? tender, Guid? operationId = null) =>
        service.CompleteScannedSale(
            _organizationId, _branchId, _operatorId, saleId, [Line(saleId)], 855m, operationId ?? Guid.NewGuid(), Guid.NewGuid(),
            tender: tender);

    private BranchOutboxCommitResult CommitManual(
        BranchNodeService service, Guid saleId, SaleTender? tender, Guid? customerId = null, Guid? operationId = null) =>
        service.CompleteOfflineSale(
            _organizationId, _branchId, _operatorId, saleId, 500m, operationId ?? Guid.NewGuid(), Guid.NewGuid(),
            customerId: customerId, tender: tender);

    [Fact]
    public void CashScannedSale_PersistsTheTenderLocally_AndQueuesItInThePayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        SaleTenderRules.TryCash(855m, 1000m, out var tender);

        CommitScanned(NewService(store), saleId, tender);

        var effect = store.GetSaleEffect(saleId)!;
        Assert.Equal(new SaleTender("cash", 1000m, 145m), effect.Tender);
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload);
        Assert.Equal(new SaleTender("cash", 1000m, 145m), payload.Tender);
    }

    [Theory]
    [InlineData("card")]
    [InlineData("qr")]
    public void CardOrQrSale_RecordsTheMethodWithNoCashFields(string method)
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        var tender = method == "card" ? SaleTenderRules.Card() : SaleTenderRules.Qr();

        CommitScanned(NewService(store), saleId, tender);

        var effect = store.GetSaleEffect(saleId)!;
        Assert.Equal(new SaleTender(method, null, null), effect.Tender);
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload);
        Assert.Equal(method, payload.Tender!.Method);
        Assert.Null(payload.Tender.AmountReceived);
        Assert.Null(payload.Tender.ChangeGiven);
    }

    [Fact]
    public void ManualSale_RecordsTheTenderAndTheSelectedCustomer()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        SaleTenderRules.TryCash(500m, 500m, out var tender);

        CommitManual(NewService(store), saleId, tender, customerId);

        var effect = store.GetSaleEffect(saleId)!;
        Assert.Equal("Manual", effect.SaleKind);
        Assert.Equal(customerId, effect.CustomerId);
        Assert.Equal(new SaleTender("cash", 500m, 0m), effect.Tender);
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload);
        Assert.Equal(customerId, payload.CustomerId);
        Assert.Equal(new SaleTender("cash", 500m, 0m), payload.Tender);
    }

    [Fact]
    public void ManualWalkInSale_CarriesNoCustomer()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();

        CommitManual(NewService(store), saleId, SaleTenderRules.Card());

        Assert.Null(store.GetSaleEffect(saleId)!.CustomerId);
        Assert.Null(SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload).CustomerId);
    }

    [Fact]
    public void SaleWithoutATender_CarriesNone()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();

        CommitScanned(NewService(store), saleId, tender: null);

        Assert.Null(store.GetSaleEffect(saleId)!.Tender);
        Assert.Null(SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload).Tender);
    }

    [Fact]
    public void Replaying_TheSameOperation_ReturnsTheStoredTender_WithoutASecondSale()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        SaleTenderRules.TryCash(855m, 900m, out var tender);

        var first = CommitScanned(service, saleId, tender, operationId);
        var replay = CommitScanned(service, saleId, tender, operationId);

        Assert.True(first.WasNewlyCommitted);
        Assert.False(replay.WasNewlyCommitted);
        Assert.Equal(new SaleTender("cash", 900m, 45m), replay.Effect.Tender);
        Assert.Single(store.GetPendingOutbox(_branchId));
    }

    [Fact]
    public void AnOlderBranchDatabaseWithoutTheTenderColumns_OpensUpgradesAndStillReadsItsSales()
    {
        using (var raw = new SqliteConnection(ConnectionString))
        {
            raw.Open();
            using var create = raw.CreateCommand();
            create.CommandText = """
                CREATE TABLE sale_effects (
                    sale_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, total_amount TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL, sale_kind TEXT NOT NULL DEFAULT 'Manual', customer_id TEXT NULL);
                INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc) VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '10', '2026-01-01T00:00:00.0000000+00:00');
                """;
            create.ExecuteNonQuery();
        }

        using (var first = new BranchSyncStore(ConnectionString))
        {
            Assert.Null(first.GetSaleEffect(Guid.Parse("11111111-1111-1111-1111-111111111111"))!.Tender);
        }

        // Opening again is idempotent and a new sale can carry a tender.
        using var second = new BranchSyncStore(ConnectionString);
        var saleId = Guid.NewGuid();
        CommitScanned(NewService(second), saleId, SaleTenderRules.Qr());
        Assert.Equal("qr", second.GetSaleEffect(saleId)!.Tender!.Method);
    }

    [Fact]
    public void PayloadFromBeforeTenders_StillDeserializes()
    {
        const string legacy =
            """{"saleId":"11111111-1111-1111-1111-111111111111","totalAmount":10,"saleKind":"Manual","occurredAtUtc":"2026-01-01T00:00:00+00:00","lines":[]}""";

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(legacy);

        Assert.Equal(10m, payload.TotalAmount);
        Assert.Null(payload.Tender);
    }
}
