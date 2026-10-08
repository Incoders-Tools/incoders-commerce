using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// pos-cash-session, branch side: opening (one per terminal), sales linked to
/// the open session in the same atomic commit and refused without one, closing
/// with computed totals and a recorded difference, resuming across restarts,
/// the queued payloads, and an older database upgrading in place.
/// </summary>
public sealed class CashSessionCommitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-cash-session-{Guid.NewGuid():N}.db");
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

    private CashSessionOpenResult Open(BranchNodeService service, decimal openingFloat = 5000m, Guid? operatorId = null) =>
        service.OpenCashSession(_organizationId, _branchId, operatorId ?? _operatorId, openingFloat, Guid.NewGuid());

    private BranchOutboxCommitResult Scanned(BranchNodeService service, decimal total, SaleTender? tender, Guid? actor = null, Guid? saleId = null)
    {
        var id = saleId ?? Guid.NewGuid();
        return service.CompleteScannedSale(
            _organizationId, _branchId, actor ?? _operatorId, id,
            [new SaleLine(id, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, total, total)],
            total, Guid.NewGuid(), Guid.NewGuid(), tender: tender);
    }

    private BranchOutboxCommitResult Manual(BranchNodeService service, decimal total, SaleTender? tender) =>
        service.CompleteOfflineSale(_organizationId, _branchId, _operatorId, Guid.NewGuid(), total, Guid.NewGuid(), Guid.NewGuid(), tender: tender);

    [Fact]
    public void OpeningASession_RecordsOperatorFloatAndMoment()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);

        var result = Open(service, 5000m);

        Assert.Equal(CashSessionOpenOutcome.Opened, result.Outcome);
        var open = service.GetOpenCashSession()!;
        Assert.Equal(result.Session!.SessionId, open.SessionId);
        Assert.Equal(_operatorId, open.OpenedByOperatorId);
        Assert.Equal(5000m, open.OpeningFloat);
        Assert.True(open.IsOpen);
    }

    [Fact]
    public void ASecondOpen_IsRefused_AndTheFirstSessionIsUntouched()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var first = Open(service, 5000m).Session!;

        var second = Open(service, 100m, Guid.NewGuid());

        Assert.Equal(CashSessionOpenOutcome.AlreadyOpen, second.Outcome);
        var open = service.GetOpenCashSession()!;
        Assert.Equal(first.SessionId, open.SessionId);
        Assert.Equal(5000m, open.OpeningFloat);
        Assert.Equal(_operatorId, open.OpenedByOperatorId);
        Assert.Single(store.GetPendingOutbox(_branchId));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10.005")]
    public void InvalidFloat_IsRefused_AndNothingIsCreated(string openingFloat)
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);

        var result = Open(service, decimal.Parse(openingFloat, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(CashSessionOpenOutcome.InvalidFloat, result.Outcome);
        Assert.Null(service.GetOpenCashSession());
        Assert.Empty(store.GetPendingOutbox(_branchId));
    }

    [Fact]
    public void SaleWithoutAnOpenSession_IsRefusedWithATypedResult_AndNothingIsWritten()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var saleId = Guid.NewGuid();

        var scanned = Scanned(service, 100m, SaleTenderRules.Card(), saleId: saleId);
        var manual = Manual(service, 100m, SaleTenderRules.Card());

        Assert.False(scanned.WasNewlyCommitted);
        Assert.Equal(SaleCommitRefusal.NoOpenCashSession, scanned.Refusal);
        Assert.Equal(SaleCommitRefusal.NoOpenCashSession, manual.Refusal);
        Assert.Null(store.GetSaleEffect(saleId));
        Assert.Empty(store.ListSaleLines(saleId));
        Assert.Empty(store.GetPendingOutbox(_branchId));
    }

    [Fact]
    public void ScannedAndManualSales_AreLinkedToTheOpenSession_InLocalRecordAndPayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service).Session!.SessionId;

        var scanned = Scanned(service, 100m, SaleTenderRules.Card());
        var manual = Manual(service, 50m, SaleTenderRules.Qr());

        Assert.Null(scanned.Refusal);
        Assert.Equal(sessionId, store.GetSaleEffect(scanned.Effect.SaleId)!.CashSessionId);
        Assert.Equal(sessionId, store.GetSaleEffect(manual.Effect.SaleId)!.CashSessionId);
        var salePayloads = store.GetPendingOutbox(_branchId).Where(e => e.PayloadKind == "sale")
            .Select(e => SyncPayloadCodec.Deserialize<SalePayloadV1>(e.Payload)).ToList();
        Assert.Equal(2, salePayloads.Count);
        Assert.All(salePayloads, p => Assert.Equal(sessionId, p.CashSessionId));
    }

    [Fact]
    public void ReplayingACommittedSale_ReturnsTheSameSale_EvenAfterTheSessionClosed()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service).Session!.SessionId;
        var saleId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var lines = new[] { new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 100m, 100m) };
        service.CompleteScannedSale(_organizationId, _branchId, _operatorId, saleId, lines, 100m, operationId, Guid.NewGuid(), tender: SaleTenderRules.Card());
        service.CloseCashSession(sessionId, _operatorId, 5000m, Guid.NewGuid());

        var replay = service.CompleteScannedSale(_organizationId, _branchId, _operatorId, saleId, lines, 100m, operationId, Guid.NewGuid(), tender: SaleTenderRules.Card());

        Assert.False(replay.WasNewlyCommitted);
        Assert.Null(replay.Refusal);
        Assert.Equal(saleId, replay.Effect.SaleId);
    }

    [Fact]
    public void Summary_ComputesTotalsFromTheRecordedTenders_IncludingDiscountedTotals()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service, 5000m).Session!.SessionId;
        SaleTenderRules.TryCash(855m, 1000m, out var cashWithChange);
        SaleTenderRules.TryCash(100m, 100m, out var exactCash);
        Scanned(service, 855m, cashWithChange);
        Scanned(service, 100m, exactCash);
        Manual(service, 300m, SaleTenderRules.Card());
        Scanned(service, 200m, SaleTenderRules.Qr());

        var summary = service.GetCashSessionSummary(sessionId)!;

        Assert.Equal(4, summary.SaleCount);
        Assert.Equal(955m, summary.CashKept);
        Assert.Equal(300m, summary.CardTotal);
        Assert.Equal(200m, summary.QrTotal);
        Assert.Equal(5955m, summary.ExpectedCash);
    }

    [Fact]
    public void Closing_RecordsTheCountedCashAndDifference_AndQueuesThePayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service, 5000m).Session!.SessionId;
        SaleTenderRules.TryCash(855m, 1000m, out var cash);
        Scanned(service, 855m, cash);
        Scanned(service, 300m, SaleTenderRules.Card());

        var closed = service.CloseCashSession(sessionId, _operatorId, 5800m, Guid.NewGuid());

        Assert.Equal(CashSessionCloseOutcome.Closed, closed.Outcome);
        var session = closed.Session!;
        Assert.False(session.IsOpen);
        Assert.Equal(5800m, session.Closure!.CountedCash);
        Assert.Equal(5855m, session.Closure.Summary.ExpectedCash);
        Assert.Equal(-55m, session.Closure.Difference);
        Assert.Null(service.GetOpenCashSession());
        var payload = SyncPayloadCodec.Deserialize<CashSessionClosedPayloadV1>(
            Assert.Single(store.GetPendingOutbox(_branchId), e => e.PayloadKind == CashSessionPayloadKinds.Closed).Payload);
        Assert.Equal(sessionId, payload.SessionId);
        Assert.Equal(2, payload.SaleCount);
        Assert.Equal(300m, payload.CardTotal);
        Assert.Equal(5855m, payload.ExpectedCash);
        Assert.Equal(5800m, payload.CountedCash);
        Assert.Equal(-55m, payload.Difference);
    }

    [Fact]
    public void ClosedSession_AcceptsNoSalesAndCannotBeClosedAgain()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service, 1000m).Session!.SessionId;
        service.CloseCashSession(sessionId, _operatorId, 1000m, Guid.NewGuid());

        var sale = Scanned(service, 100m, SaleTenderRules.Card());
        var again = service.CloseCashSession(sessionId, _operatorId, 1m, Guid.NewGuid());

        Assert.Equal(SaleCommitRefusal.NoOpenCashSession, sale.Refusal);
        Assert.Equal(CashSessionCloseOutcome.AlreadyClosed, again.Outcome);
        Assert.Equal(1000m, service.GetCashSession(sessionId)!.Closure!.CountedCash);
        Assert.Single(store.GetPendingOutbox(_branchId), e => e.PayloadKind == CashSessionPayloadKinds.Closed);
    }

    [Fact]
    public void Closing_RefusesAnInvalidCountedCash_AndKeepsTheSessionOpen()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service).Session!.SessionId;

        var result = service.CloseCashSession(sessionId, _operatorId, -5m, Guid.NewGuid());

        Assert.Equal(CashSessionCloseOutcome.InvalidCountedCash, result.Outcome);
        Assert.NotNull(service.GetOpenCashSession());
    }

    [Fact]
    public void ANewSession_CanBeOpenedAfterClosing_WithItsOwnTotals()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var first = Open(service).Session!.SessionId;
        Scanned(service, 100m, SaleTenderRules.Card());
        service.CloseCashSession(first, _operatorId, 5000m, Guid.NewGuid());

        var second = Open(service, 200m);

        Assert.Equal(CashSessionOpenOutcome.Opened, second.Outcome);
        Assert.NotEqual(first, second.Session!.SessionId);
        Assert.Equal(0, service.GetCashSessionSummary(second.Session.SessionId)!.SaleCount);
    }

    [Fact]
    public void SwitchingOperator_KeepsTheSession_AndEachSaleKeepsItsOperator()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);
        var sessionId = Open(service).Session!.SessionId;
        var operatorB = Guid.NewGuid();

        var byA = Scanned(service, 100m, SaleTenderRules.Card());
        var byB = Scanned(service, 100m, SaleTenderRules.Card(), actor: operatorB);

        Assert.Equal(sessionId, store.GetSaleEffect(byA.Effect.SaleId)!.CashSessionId);
        Assert.Equal(sessionId, store.GetSaleEffect(byB.Effect.SaleId)!.CashSessionId);
        var actors = store.GetPendingOutbox(_branchId).Where(e => e.PayloadKind == "sale").Select(e => e.ActorId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { _operatorId, operatorB }, actors);
        Assert.Equal(_operatorId, service.GetOpenCashSession()!.OpenedByOperatorId);
    }

    [Fact]
    public void AnOpenSession_ResumesAfterARestart_WithItsSalesAndFloat()
    {
        Guid sessionId;
        using (var store = new BranchSyncStore(ConnectionString))
        {
            var service = NewService(store);
            sessionId = Open(service, 750m).Session!.SessionId;
            Scanned(service, 100m, SaleTenderRules.Card());
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new BranchSyncStore(ConnectionString);
        var again = NewService(reopened);

        var open = again.GetOpenCashSession()!;
        Assert.Equal(sessionId, open.SessionId);
        Assert.Equal(750m, open.OpeningFloat);
        Assert.Equal(1, again.GetCashSessionSummary(sessionId)!.SaleCount);
    }

    [Fact]
    public void OpeningQueuesAnOpenedPayload_WithTheOperatorAndFloat()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);

        var sessionId = Open(service, 5000m).Session!.SessionId;

        var envelope = Assert.Single(store.GetPendingOutbox(_branchId), e => e.PayloadKind == CashSessionPayloadKinds.Opened);
        Assert.Equal(sessionId, envelope.AggregateId);
        Assert.Equal(_operatorId, envelope.ActorId);
        var payload = SyncPayloadCodec.Deserialize<CashSessionOpenedPayloadV1>(envelope.Payload);
        Assert.Equal(_operatorId, payload.OperatorId);
        Assert.Equal(5000m, payload.OpeningFloat);
    }

    [Fact]
    public void ADatabaseFromBeforeCashSessions_OpensAndUpgradesInPlace()
    {
        using (var old = new SqliteConnection(ConnectionString))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE sale_effects (
                    sale_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, total_amount TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL, sale_kind TEXT NOT NULL DEFAULT 'Manual', customer_id TEXT NULL);
                INSERT INTO sale_effects VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', '10', '2026-01-01T00:00:00.0000000+00:00', 'Manual', NULL);
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new BranchSyncStore(ConnectionString);
        var service = NewService(store);

        Assert.Null(store.GetSaleEffect(Guid.Parse("11111111-1111-1111-1111-111111111111"))!.CashSessionId);
        Assert.Equal(CashSessionOpenOutcome.Opened, Open(service).Outcome);
    }

    [Fact]
    public void ReopeningTheStoreTwice_DoesNotFail()
    {
        using (new BranchSyncStore(ConnectionString)) { }
        SqliteConnection.ClearAllPools();
        using var second = new BranchSyncStore(ConnectionString);
        Assert.Null(second.GetOpenCashSession());
    }
}
