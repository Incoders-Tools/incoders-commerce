using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The POS sales history and sale voids, in the branch database: sales listed per day newest first; a void is its own
/// record (the sale is never changed), queued as a <c>sale.voided</c> envelope in the same transaction, it takes the sale
/// out of its cash session's totals, and it is refused for a sale already voided or of a closed session.
/// </summary>
public sealed class PosSaleHistoryTests : IDisposable
{
    private static readonly DiscountAuthorization Pin = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 4);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-history-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _branchId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();
    private DateTimeOffset _now = new(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);

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

    private BranchNodeService NewService(BranchSyncStore store)
    {
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink, () => _now);
    }

    private Guid Sell(BranchNodeService service, decimal total, SaleTender tender, Guid? customerId = null)
    {
        var id = Guid.NewGuid();
        var result = service.CompleteScannedSale(
            _organizationId, _branchId, _operatorId, id,
            [new SaleLine(id, 1, Guid.NewGuid(), "c1", "Vacío", "Por kg", 1m, total, total)],
            total, Guid.NewGuid(), Guid.NewGuid(), customerId: customerId, tender: tender);
        Assert.True(result.WasNewlyCommitted);
        _now = _now.AddMinutes(5);
        return id;
    }

    private SaleVoidResult Void(BranchNodeService service, Guid saleId, string reason = "Cobro duplicado") =>
        service.VoidSale(_organizationId, _branchId, _operatorId, saleId, Pin, reason, Guid.NewGuid());

    private static (DateTimeOffset, DateTimeOffset) Day => (new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheDaysSales_AreListedNewestFirst_WithTheirCustomerAndTender()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        service.OpenCashSession(_organizationId, _branchId, _operatorId, 1000m, Guid.NewGuid());
        var customer = Guid.NewGuid();
        store.UpsertCustomers([new CustomerReplica(customer, _organizationId, "Parrilla Don Julio", "Wholesale", null, null, null, _now)]);
        var first = Sell(service, 100m, SaleTenderRules.Card());
        var second = Sell(service, 250m, SaleTenderRules.Qr(), customer);

        var (from, to) = Day;
        var sales = service.ListSales(from, to);

        Assert.Equal([second, first], sales.Select(s => s.SaleId));
        Assert.Equal(("Parrilla Don Julio", "qr", 1), (sales[0].CustomerName, sales[0].Tender!.Method, sales[0].LineCount));
        Assert.Null(sales[1].CustomerName);
        Assert.Empty(service.ListSales(from.AddDays(1), to.AddDays(1)));
    }

    [Fact]
    public void Voiding_RecordsTheVoid_QueuesItsEnvelope_AndTheSessionStopsCountingTheSale()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        var session = service.OpenCashSession(_organizationId, _branchId, _operatorId, 1000m, Guid.NewGuid()).Session!.SessionId;
        Assert.True(SaleTenderRules.TryCash(300m, 500m, out var cash));
        var voided = Sell(service, 300m, cash);
        Sell(service, 100m, SaleTenderRules.Card());
        Assert.Equal(1300m, service.GetCashSessionSummary(session)!.ExpectedCash);

        var result = Void(service, voided, "  Cobro duplicado  ");

        Assert.Equal(SaleVoidOutcome.Voided, result.Outcome);
        var summary = service.GetCashSessionSummary(session)!;
        Assert.Equal((1, 1000m, 100m), (summary.SaleCount, summary.ExpectedCash, summary.CardTotal));

        var record = store.GetSaleVoid(voided)!;
        Assert.Equal(("Cobro duplicado", Pin, session), (record.Reason, record.Authorization, record.CashSessionId));
        Assert.NotNull(service.GetSaleEffect(voided)); // the sale itself is untouched

        var envelope = Assert.Single(store.GetPendingOutbox(_branchId), e => e.PayloadKind == SalePayloadKinds.Voided);
        Assert.Equal((voided, 2L), (envelope.AggregateId, envelope.AggregateVersion));
        var payload = SyncPayloadCodec.Deserialize<SaleVoidedPayloadV1>(envelope.Payload);
        Assert.Equal((voided, 300m, "Cobro duplicado", "cash"), (payload.SaleId, payload.TotalAmount, payload.Reason, payload.Tender!.Method));
        Assert.Equal(Pin, payload.Authorization);

        var (from, to) = Day;
        Assert.NotNull(service.ListSales(from, to).Single(s => s.SaleId == voided).Void);
    }

    [Fact]
    public void TheOutbox_IsPushedInTheOrderItWasWritten_SoASaleTravelsBeforeItsVoid()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        service.OpenCashSession(_organizationId, _branchId, _operatorId, 0m, Guid.NewGuid());
        var sale = Sell(service, 100m, SaleTenderRules.Card());

        Void(service, sale);

        var kinds = store.GetPendingOutbox(_branchId).Select(e => e.PayloadKind).ToList();
        Assert.Equal(["cash-session.opened", "sale", SalePayloadKinds.Voided], kinds);
    }

    [Fact]
    public void ASaleIsVoidedOnce_AndNeverOnceItsSessionClosed()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        var session = service.OpenCashSession(_organizationId, _branchId, _operatorId, 0m, Guid.NewGuid()).Session!.SessionId;
        var voided = Sell(service, 100m, SaleTenderRules.Card());
        var closedLater = Sell(service, 200m, SaleTenderRules.Card());

        Assert.Equal(SaleVoidOutcome.Voided, Void(service, voided).Outcome);
        Assert.Equal(SaleVoidOutcome.AlreadyVoided, Void(service, voided).Outcome);

        service.CloseCashSession(session, _operatorId, 0m, Guid.NewGuid());
        Assert.Equal(SaleVoidOutcome.SessionNotOpen, Void(service, closedLater).Outcome);
        Assert.Equal(SaleVoidOutcome.NotFound, Void(service, Guid.NewGuid()).Outcome);
        Assert.Single(store.GetPendingOutbox(_branchId), e => e.PayloadKind == SalePayloadKinds.Voided);
        Assert.Equal(1, service.GetCashSession(session)!.Closure!.Summary.SaleCount); // the close already left the void out
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVoidWithoutAReason_IsRefused(string reason)
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        service.OpenCashSession(_organizationId, _branchId, _operatorId, 0m, Guid.NewGuid());
        var sale = Sell(service, 100m, SaleTenderRules.Card());

        Assert.Throws<ArgumentException>(() => Void(service, sale, reason));
        Assert.Null(store.GetSaleVoid(sale));
    }

    [Fact]
    public void TheHistory_OffersTheVoidOnlyForAnOpenSessionSale_AndSearchesAndSums()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = NewService(store);
        var session = service.OpenCashSession(_organizationId, _branchId, _operatorId, 0m, Guid.NewGuid()).Session!.SessionId;
        var kept = Sell(service, 100m, SaleTenderRules.Card());
        var voided = Sell(service, 250m, SaleTenderRules.Qr());
        Void(service, voided);
        var (from, to) = Day;

        var open = SaleHistory.Rows(service.ListSales(from, to), session);
        Assert.Equal([(voided, false), (kept, true)], open.Select(r => (r.SaleId, r.CanVoid)));
        Assert.Null(SaleHistory.VoidUnavailableReason(open[1]));

        var afterClose = SaleHistory.Rows(service.ListSales(from, to), openCashSessionId: null);
        Assert.False(afterClose[1].CanVoid);
        Assert.Contains("caja abierta", SaleHistory.VoidUnavailableReason(afterClose[1]));

        Assert.Equal([voided], SaleHistory.Filter(open, "qr").Select(r => r.SaleId));
        Assert.Equal([voided], SaleHistory.Filter(open, "anulada").Select(r => r.SaleId));
        Assert.Equal([kept], SaleHistory.Filter(open, "tarjeta").Select(r => r.SaleId));
        Assert.StartsWith("1 venta ·", SaleHistory.Summary(open));
        Assert.EndsWith("· 1 anulado", SaleHistory.Summary(open));
    }

    [Fact]
    public void ALocalDay_IsMappedToItsUtcRange()
    {
        var argentina = TimeZoneInfo.CreateCustomTimeZone("ART", TimeSpan.FromHours(-3), "ART", "ART");

        var (from, to) = SaleHistory.DayRange(new DateOnly(2026, 10, 5), argentina);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero), to);
    }
}
