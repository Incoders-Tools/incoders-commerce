using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Domain.Tenancy;
using Microsoft.Data.Sqlite;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale "Sale Number": the terminal numbers every sale from a per-(branch,
/// register) counter that lives in the SAME SQLite transaction as the sale, after the
/// idempotency check; a terminal whose register is not known yet commits without a
/// number; nothing is ever renumbered.
/// </summary>
public sealed class SaleNumberingCommitTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-sale-number-{Guid.NewGuid():N}.db");
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _branchId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();

    private string ConnectionString => $"Data Source={_dbPath}";

    private static readonly SaleNumbering Register2 = new(new BranchCode(1), new RegisterNumber(2));
    private static readonly SaleNumbering Register3 = new(new BranchCode(1), new RegisterNumber(3));

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

    private static BranchNodeService Service(BranchSyncStore store, bool openSession = true)
    {
        var sink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(sink), sink);
        return openSession ? service.WithOpenSession() : service;
    }

    private BranchOutboxCommitResult Manual(BranchNodeService service, SaleNumbering? numbering, Guid? operationId = null, Guid? saleId = null) =>
        service.CompleteOfflineSale(
            _organizationId, _branchId, _operatorId, saleId ?? Guid.NewGuid(), 500m, operationId ?? Guid.NewGuid(), Guid.NewGuid(),
            numbering: numbering);

    private BranchOutboxCommitResult Scanned(BranchNodeService service, SaleNumbering? numbering)
    {
        var saleId = Guid.NewGuid();
        return service.CompleteScannedSale(
            _organizationId, _branchId, _operatorId, saleId,
            [new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 855m, 855m)], 855m, Guid.NewGuid(), Guid.NewGuid(),
            numbering: numbering);
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public void ConsecutiveSales_GetConsecutiveNumbers_StampedOnTheEffectAndTheQueuedPayload()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);

        var first = Manual(service, Register2);
        var second = Scanned(service, Register2);

        Assert.Equal("V01-C2-1", first.Effect.Number!.Value.Format());
        Assert.Equal("V01-C2-2", second.Effect.Number!.Value.Format());
        Assert.Equal("V01-C2-2", store.GetSaleEffect(second.Effect.SaleId)!.Number!.Value.Format());
        var payloads = store.GetPendingOutbox(_branchId).Where(o => o.PayloadKind == "sale")
            .Select(o => SyncPayloadCodec.Deserialize<SalePayloadV1>(o.Payload)).ToList();
        Assert.Equal(new[] { 1, 2 }, payloads.Select(p => p.SaleSequence!.Value).Order());
        Assert.All(payloads, p => Assert.Equal((1, 2), (p.BranchCode, p.RegisterNumber)));
    }

    [Fact]
    public void TheCounter_IsKeptPerBranchAndRegister()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);

        Manual(service, Register2);
        Manual(service, Register2);
        var otherRegister = Manual(service, Register3);
        var backOnTheFirst = Manual(service, Register2);

        Assert.Equal("V01-C3-1", otherRegister.Effect.Number!.Value.Format());
        Assert.Equal("V01-C2-3", backOnTheFirst.Effect.Number!.Value.Format());
    }

    [Fact]
    public void AnIdempotentRetry_ReturnsTheOriginalNumber_AndDoesNotAdvanceTheCounter()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);
        var operationId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        var original = Manual(service, Register2, operationId, saleId);
        var retry = Manual(service, Register2, operationId, saleId);
        var next = Manual(service, Register2);

        Assert.False(retry.WasNewlyCommitted);
        Assert.Equal("V01-C2-1", retry.Effect.Number!.Value.Format());
        Assert.Equal(original.Effect.Number, retry.Effect.Number);
        Assert.Equal("V01-C2-2", next.Effect.Number!.Value.Format());
    }

    [Fact]
    public void AnIdempotentRetry_EvenFromATerminalThatLaterChangedRegister_KeepsTheOriginalNumber()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);
        var operationId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        Manual(service, Register2, operationId, saleId);

        var retry = Manual(service, Register3, operationId, saleId);

        Assert.Equal("V01-C2-1", retry.Effect.Number!.Value.Format());
        Assert.Equal(0, Scalar("SELECT COUNT(1) FROM terminal_counters WHERE register_number = 3"));
    }

    [Fact]
    public void WithoutAKnownRegister_TheSaleCommitsWithoutANumber_AndNoCounterIsCreated()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);

        var result = Manual(service, numbering: null);

        Assert.True(result.WasNewlyCommitted);
        Assert.Null(result.Effect.Number);
        Assert.Null(store.GetSaleEffect(result.Effect.SaleId)!.SaleSequence);
        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(Assert.Single(store.GetPendingOutbox(_branchId)).Payload);
        Assert.Null(payload.SaleSequence);
        Assert.Null(payload.RegisterNumber);
        Assert.Equal(0, Scalar("SELECT COUNT(1) FROM terminal_counters"));
    }

    [Fact]
    public void WhenTheRegisterBecomesKnown_LaterSalesAreNumbered_AndEarlierOnesAreNeverRenumbered()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);
        var early = Manual(service, numbering: null);

        var later = Manual(service, Register2);

        Assert.Equal("V01-C2-1", later.Effect.Number!.Value.Format());
        Assert.Null(store.GetSaleEffect(early.Effect.SaleId)!.Number);
    }

    [Fact]
    public void TheCounter_IsSeededFromTheHighestLocalSequence_WhenItsRowIsMissing()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var service = Service(store);
        Manual(service, Register2);
        Manual(service, Register2);
        Manual(service, Register2);
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var wipe = connection.CreateCommand();
            wipe.CommandText = "DELETE FROM terminal_counters;";
            wipe.ExecuteNonQuery();
        }

        var next = Manual(service, Register2);

        Assert.Equal("V01-C2-4", next.Effect.Number!.Value.Format());
        Assert.Equal(4, Scalar("SELECT last_sequence FROM terminal_counters WHERE register_number = 2"));
    }

    [Fact]
    public void ASaleRefusedForWantOfACashSession_DoesNotBurnANumber()
    {
        using var store = new BranchSyncStore(ConnectionString);
        var withoutSession = Service(store, openSession: false);

        var refused = Manual(withoutSession, Register2);
        var accepted = Manual(Service(store), Register2);

        Assert.NotNull(refused.Refusal);
        Assert.Equal("V01-C2-1", accepted.Effect.Number!.Value.Format());
    }

    [Fact]
    public void ANumberedSale_SurvivesReopeningTheDatabase()
    {
        Guid saleId;
        using (var store = new BranchSyncStore(ConnectionString))
        {
            saleId = Manual(Service(store), Register2).Effect.SaleId;
        }

        using var reopened = new BranchSyncStore(ConnectionString);
        var next = Manual(Service(reopened), Register2);

        Assert.Equal("V01-C2-1", reopened.GetSaleEffect(saleId)!.Number!.Value.Format());
        Assert.Equal("V01-C2-2", next.Effect.Number!.Value.Format());
    }

    [Fact]
    public void ABranchDatabaseFromBeforeNumbering_GainsTheColumns_AndItsOldSalesStayUnnumbered()
    {
        var oldSale = Guid.NewGuid();
        using (var connection = new SqliteConnection(ConnectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = $"""
                CREATE TABLE sale_effects (
                    sale_id TEXT PRIMARY KEY, branch_id TEXT NOT NULL, total_amount TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL, sale_kind TEXT NOT NULL DEFAULT 'Manual', customer_id TEXT NULL);
                INSERT INTO sale_effects (sale_id, branch_id, total_amount, occurred_at_utc)
                VALUES ('{oldSale}', '{_branchId}', '10', '2026-01-01T00:00:00.0000000+00:00');
                """;
            create.ExecuteNonQuery();
        }

        using var store = new BranchSyncStore(ConnectionString);
        var fresh = Manual(Service(store), Register2);

        Assert.Null(store.GetSaleEffect(oldSale)!.Number);
        Assert.Equal("V01-C2-1", fresh.Effect.Number!.Value.Format());
    }
}
