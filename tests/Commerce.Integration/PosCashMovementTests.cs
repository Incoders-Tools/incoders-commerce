using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Cash movements of the drawer at the POS ("movimiento de caja"): money taken out (a withdrawal, with the branch PIN,
/// never more than the drawer should hold) or put in, outside a sale, only in the open session. They change the expected
/// cash, travel in their own envelope, are frozen in the close and its payload, and show in the history.
/// </summary>
public sealed class PosCashMovementTests : IDisposable
{
    private static readonly DiscountAuthorization Pin = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 3);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-cash-movements-{Guid.NewGuid():N}.db");
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly Guid _cashier = Guid.NewGuid();
    private DateTimeOffset _now = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero);

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

    private BranchNodeService Service(BranchSyncStore store)
    {
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink, () => _now);
    }

    private CashMovementResult Move(BranchNodeService service, string kind, string counterpart, decimal amount, DiscountAuthorization? pin = null)
    {
        var result = service.RecordCashMovement(_org, _branch, _cashier, kind, counterpart, amount, "Motivo de prueba", pin, Guid.NewGuid());
        _now = _now.AddMinutes(1);
        return result;
    }

    private void CashSale(BranchNodeService service, decimal total)
    {
        var sale = Guid.NewGuid();
        Assert.True(SaleTenderRules.TryCash(total, total, out var cash));
        service.CompleteScannedSale(_org, _branch, _cashier, sale,
            [new SaleLine(sale, 1, Guid.NewGuid(), null, "Vacío", "Por kg", 1m, total, total)], total, Guid.NewGuid(), Guid.NewGuid(),
            tender: cash);
        _now = _now.AddMinutes(1);
    }

    [Fact]
    public void Withdrawals_AndDeposits_ChangeTheExpectedCash_AndTravelInTheirOwnEnvelope()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        Assert.Equal(CashMovementOutcome.NoOpenCashSession, Move(service, CashMovement.Deposit, CashMovement.Other, 100m).Outcome);

        var session = service.OpenCashSession(_org, _branch, _cashier, 5_000m, Guid.NewGuid()).Session!.SessionId;
        CashSale(service, 20_000m);
        var withdrawal = Move(service, CashMovement.Withdrawal, CashMovement.Safe, 15_000m, Pin);
        Assert.Equal(CashMovementOutcome.Recorded, withdrawal.Outcome);
        Assert.Equal(CashMovementOutcome.Recorded, Move(service, CashMovement.Deposit, CashMovement.Safe, 2_000m).Outcome);

        var summary = service.GetCashSessionSummary(session)!;
        Assert.Equal((15_000m, 2_000m, 2, 12_000m), (summary.CashWithdrawn, summary.CashDeposited, summary.CashMovementCount, summary.ExpectedCash));

        var envelope = store.GetPendingOutbox(_branch)
            .First(e => e.PayloadKind == CashMovementPayloadKinds.Recorded && e.AggregateId == withdrawal.Movement!.MovementId);
        var payload = SyncPayloadCodec.Deserialize<CashMovementRecordedPayloadV1>(envelope.Payload);
        Assert.Equal((session, CashMovement.Withdrawal, CashMovement.Safe, 15_000m, "Motivo de prueba", Pin),
            (payload.CashSessionId, payload.Kind, payload.Counterpart, payload.Amount, payload.Reason, payload.Authorization));

        var closed = service.CloseCashSession(session, _cashier, 11_900m, Guid.NewGuid()).Session!.Closure!;
        Assert.Equal(-100m, closed.Difference);
        Assert.Equal(15_000m, service.GetCashSession(session)!.Closure!.Summary.CashWithdrawn); // stored, not only computed
        var close = SyncPayloadCodec.Deserialize<CashSessionClosedPayloadV1>(
            store.GetPendingOutbox(_branch).Single(e => e.PayloadKind == CashSessionPayloadKinds.Closed).Payload);
        Assert.Equal((15_000m, 2_000m, 12_000m, -100m), (close.CashWithdrawn, close.CashDeposited, close.ExpectedCash, close.Difference));
    }

    [Fact]
    public void AWithdrawal_NeedsThePin_AndCannotTakeMoreThanTheDrawerHolds()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        service.OpenCashSession(_org, _branch, _cashier, 1_000m, Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => Move(service, CashMovement.Withdrawal, CashMovement.Expense, 500m, pin: null));
        Assert.Throws<ArgumentException>(() => Move(service, CashMovement.Deposit, CashMovement.Bank, 500m)); // a deposit never comes "from the bank"
        Assert.Throws<ArgumentException>(() => Move(service, CashMovement.Deposit, CashMovement.Other, 0m));
        Assert.Throws<ArgumentException>(() =>
            service.RecordCashMovement(_org, _branch, _cashier, CashMovement.Deposit, CashMovement.Other, 10m, "  ", null, Guid.NewGuid()));

        Assert.Equal(CashMovementOutcome.ExceedsExpectedCash, Move(service, CashMovement.Withdrawal, CashMovement.Expense, 1_000.01m, Pin).Outcome);
        var paid = Move(service, CashMovement.Withdrawal, CashMovement.Expense, 1_000m, Pin);
        Assert.Equal(CashMovementOutcome.Recorded, paid.Outcome);
        Assert.Equal("Retiro para pago de gasto", paid.Movement!.Description);

        // A deposit needs no PIN, and the PIN given with it is not kept.
        var deposit = Move(service, CashMovement.Deposit, CashMovement.Other, 50m, Pin);
        Assert.Null(deposit.Movement!.Authorization);
    }

    [Fact]
    public void TheHistory_ShowsTheMovements_WhichAreNeverVoided()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        var session = service.OpenCashSession(_org, _branch, _cashier, 1_000m, Guid.NewGuid()).Session!.SessionId;
        CashSale(service, 3_000m);
        Move(service, CashMovement.Withdrawal, CashMovement.Bank, 2_500m, Pin);
        var (from, to) = (new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));

        var rows = SaleHistory.Rows(service.ListSales(from, to), service.ListCustomerPayments(from, to), session, service.ListCashMovements(from, to));

        var movement = Assert.IsType<CashMovementHistoryRow>(rows[0]);
        Assert.False(movement.CanVoid);
        Assert.Equal(("Retiro de caja", "Retiro para depositar en banco"), (movement.NumberText, movement.CustomerText));
        Assert.EndsWith("1 movimiento de caja", SaleHistory.Summary(rows));
        Assert.Single(SaleHistory.Filter(rows, "banco"));
    }

    [Theory]
    [InlineData("Withdrawal", "5000", "Caja fuerte", 10000, true, null)]
    [InlineData("Withdrawal", "15000", "Caja fuerte", 10000, false, "No se puede retirar más")]
    [InlineData("Deposit", "15000", "Cambio", 10000, true, null)]
    [InlineData("Deposit", "15000", "", 10000, false, null)]
    [InlineData("Deposit", "0", "Cambio", 10000, false, "mayor que cero")]
    [InlineData("Deposit", "1,555", "Cambio", 10000, false, "Ingrese el importe")]
    public void ThePrompt_ValidatesTheAmountAndTheReason(string kind, string amount, string reason, double expected, bool valid, string? message)
    {
        var entry = CashMovementInput.Evaluate(kind, amount, reason, (decimal)expected);

        Assert.Equal(valid, entry.IsValid);
        if (message is null)
        {
            Assert.Null(entry.Message);
        }
        else
        {
            Assert.Contains(message, entry.Message);
        }
    }
}
