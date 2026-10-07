using System.Globalization;
using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Payments of current account debt collected at the POS ("cobros"): recorded only in an open cash session (cash ones
/// count in the expected cash), voided like a sale (open session, PIN, own envelope), listed with the sales, and the
/// customer's balance this terminal shows: the last synced balance corrected by what it did since. Plus the payment
/// terms of a sale on account as the cashier reads them.
/// </summary>
public sealed class PosCustomerPaymentTests : IDisposable
{
    private static readonly DiscountAuthorization Pin = new(DiscountAuthorization.BranchPin, Guid.NewGuid(), 2);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-customer-payments-{Guid.NewGuid():N}.db");
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _branch = Guid.NewGuid();
    private readonly Guid _cashier = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
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

    private BranchNodeService Service(BranchSyncStore store)
    {
        var sink = new InMemoryAuditSink();
        return new BranchNodeService(store, new TenantAuthorizationService(sink), sink, () => _now);
    }

    private CustomerPaymentResult Pay(BranchNodeService service, decimal amount, SaleTender tender, string? note = null)
    {
        var result = service.ReceiveCustomerPayment(_org, _branch, _cashier, _customer, amount, tender, note, Guid.NewGuid());
        _now = _now.AddMinutes(1);
        return result;
    }

    private void Snapshot(BranchSyncStore store, decimal? balance, int? customerDays, int defaultDays) =>
        store.ApplyPriceListsSync(new PriceListsReplicaSnapshot(
            _org, [], [], [], [], null, null,
            balance is { } owed ? [new CustomerBalanceReplica(_customer, owed, 1_000m)] : [],
            customerDays is { } days ? [new CustomerTermsReplica(_customer, days)] : [],
            defaultDays), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void APayment_NeedsAnOpenCashSession_AndCashOnesCountInTheExpectedCash()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        Assert.Equal(CustomerPaymentOutcome.NoOpenCashSession, Pay(service, 100m, SaleTenderRules.Card()).Outcome);

        var session = service.OpenCashSession(_org, _branch, _cashier, 1_000m, Guid.NewGuid()).Session!.SessionId;
        Assert.True(SaleTenderRules.TryCash(5_000m, 6_000m, out var cash));
        Assert.Equal(CustomerPaymentOutcome.Recorded, Pay(service, 5_000m, cash, "Pago parcial").Outcome);
        Assert.Equal(CustomerPaymentOutcome.Recorded, Pay(service, 2_000m, SaleTenderRules.Qr()).Outcome);

        var summary = service.GetCashSessionSummary(session)!;
        Assert.Equal((5_000m, 0m, 2_000m, 2, 6_000m), (summary.CollectedCash, summary.CollectedCard, summary.CollectedQr, summary.CollectionCount, summary.ExpectedCash));

        var envelope = store.GetPendingOutbox(_branch).First(e => e.PayloadKind == CustomerPaymentPayloadKinds.Received);
        var payload = SyncPayloadCodec.Deserialize<CustomerPaymentReceivedPayloadV1>(envelope.Payload);
        Assert.Equal((_customer, 5_000m, "cash", 1_000m, "Pago parcial"), (payload.CustomerId, payload.Amount, payload.Tender.Method, payload.Tender.ChangeGiven!.Value, payload.Note));
        Assert.Equal(payload.PaymentId, envelope.AggregateId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.555)]
    public void AnInvalidAmount_OrAPaymentOnAccount_IsRefused(decimal amount)
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        service.OpenCashSession(_org, _branch, _cashier, 0m, Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => Pay(service, amount, SaleTenderRules.Card()));
        Assert.Throws<ArgumentException>(() => Pay(service, 10m, SaleTenderRules.Account()));
    }

    [Fact]
    public void AVoidedPayment_LeavesTheCashSession_IsQueued_AndCannotBeVoidedTwice()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        var session = service.OpenCashSession(_org, _branch, _cashier, 0m, Guid.NewGuid()).Session!.SessionId;
        var payment = Pay(service, 3_000m, SaleTenderRules.Card()).Payment!;

        Assert.Equal(SaleVoidOutcome.Voided,
            service.VoidCustomerPayment(_org, _branch, _cashier, payment.PaymentId, Pin, "Se cobró dos veces", Guid.NewGuid()).Outcome);
        Assert.Equal(SaleVoidOutcome.AlreadyVoided,
            service.VoidCustomerPayment(_org, _branch, _cashier, payment.PaymentId, Pin, "otra vez", Guid.NewGuid()).Outcome);

        Assert.Equal(0m, service.GetCashSessionSummary(session)!.CollectedCard);
        var voided = SyncPayloadCodec.Deserialize<CustomerPaymentVoidedPayloadV1>(
            store.GetPendingOutbox(_branch).Single(e => e.PayloadKind == CustomerPaymentPayloadKinds.Voided).Payload);
        Assert.Equal((payment.PaymentId, _customer, 3_000m, "Se cobró dos veces", Pin), (voided.PaymentId, voided.CustomerId, voided.Amount, voided.Reason, voided.Authorization));

        service.CloseCashSession(session, _cashier, 0m, Guid.NewGuid());
        var other = new BranchNodeService(store, new TenantAuthorizationService(new InMemoryAuditSink()), new InMemoryAuditSink(), () => _now);
        other.OpenCashSession(_org, _branch, _cashier, 0m, Guid.NewGuid());
        var (from, to) = (new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        var listed = Assert.Single(service.ListCustomerPayments(from, to));
        Assert.NotNull(listed.Void);
    }

    [Fact]
    public void TheCustomersBalance_IsTheLastSyncedOne_CorrectedByWhatThisTerminalDidSince()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        Assert.Null(service.GetCustomerAccountView(_customer).SyncedBalance);

        Snapshot(store, balance: 20_000m, customerDays: null, defaultDays: 30);
        service.OpenCashSession(_org, _branch, _cashier, 0m, Guid.NewGuid());
        var sale = Guid.NewGuid();
        service.CompleteScannedSale(_org, _branch, _cashier, sale,
            [new SaleLine(sale, 1, Guid.NewGuid(), null, "Vacío", "Por kg", 1m, 4_000m, 4_000m)], 4_000m, Guid.NewGuid(), Guid.NewGuid(),
            customerId: _customer, tender: SaleTenderRules.Account());
        Pay(service, 10_000m, SaleTenderRules.Card());

        var view = service.GetCustomerAccountView(_customer);
        Assert.Equal((20_000m, 1_000m, 4_000m, 10_000m, 14_000m),
            (view.SyncedBalance!.Value, view.SyncedOverdue!.Value, view.PendingSalesOnAccount, view.PendingPayments, view.EstimatedBalance));
        Assert.Equal($"Debe {14_000m.ToString("C", CultureInfo.CurrentCulture)}", CustomerPaymentInput.BalanceHeadline(view));
        Assert.Contains("cobros desde entonces", CustomerPaymentInput.BalanceDetail(view));

        // Once the cloud counted them (acknowledged before a newer snapshot), they are no longer added again.
        foreach (var envelope in store.GetPendingOutbox(_branch))
        {
            store.Acknowledge(envelope.OperationId);
        }

        Thread.Sleep(5);
        Snapshot(store, balance: 14_000m, customerDays: null, defaultDays: 30);
        Assert.Equal(14_000m, service.GetCustomerAccountView(_customer).EstimatedBalance);
    }

    [Fact]
    public void ASaleOnAccount_IsDueAfterTheCustomersTerms_ElseTheOrganizationsDefault_AndTheCashierReadsWhen()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        Assert.Equal(new PaymentTerms(30, PaymentTermsSource.Organization), store.GetCustomerPaymentTerms(_customer)); // never synced

        Snapshot(store, balance: null, customerDays: null, defaultDays: 45);
        var terms = service.GetCustomerAccountView(_customer).Terms;
        Assert.Equal(new PaymentTerms(45, PaymentTermsSource.Organization), terms);
        Assert.Equal("Vence el 19/11/2026 (45 días, plazo general de la organización).", CustomerPaymentInput.DueText(terms, new DateOnly(2026, 10, 5)));
        Assert.Equal(0m, service.GetCustomerAccountView(_customer).SyncedBalance); // synced and owing nothing

        Snapshot(store, balance: null, customerDays: 0, defaultDays: 45);
        var own = store.GetCustomerPaymentTerms(_customer);
        Assert.Equal(new PaymentTerms(0, PaymentTermsSource.Customer), own);
        Assert.Equal("Vence hoy, 05/10/2026 (0 días, plazo del cliente).", CustomerPaymentInput.DueText(own, new DateOnly(2026, 10, 5)));
    }

    [Theory]
    [InlineData("15000", "cash", "20000", true, 5000.0)]
    [InlineData("15000,50", "card", null, true, null)]
    [InlineData("15000", "cash", "100", false, null)]
    [InlineData("0", "qr", null, false, null)]
    [InlineData("abc", "qr", null, false, null)]
    public void ThePaymentPrompt_ValidatesTheAmountAndTheCashReceived(string amount, string method, string? received, bool valid, double? change)
    {
        var entry = CustomerPaymentInput.Evaluate(amount, method, received);

        Assert.Equal(valid, entry.IsValid);
        Assert.Equal(change is null ? null : (decimal)change.Value, entry.Change);
    }

    [Fact]
    public void TheHistory_ListsSalesAndPayments_NewestFirst_AndSumsThemApart()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var service = Service(store);
        var session = service.OpenCashSession(_org, _branch, _cashier, 0m, Guid.NewGuid()).Session!.SessionId;
        var sale = Guid.NewGuid();
        service.CompleteScannedSale(_org, _branch, _cashier, sale,
            [new SaleLine(sale, 1, Guid.NewGuid(), null, "Vacío", "Por kg", 1m, 4_000m, 4_000m)], 4_000m, Guid.NewGuid(), Guid.NewGuid(),
            tender: SaleTenderRules.Card());
        _now = _now.AddMinutes(1);
        Pay(service, 2_500m, SaleTenderRules.Qr());
        var (from, to) = (new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

        var rows = SaleHistory.Rows(service.ListSales(from, to), service.ListCustomerPayments(from, to), session);

        Assert.IsType<PaymentHistoryRow>(rows[0]);
        Assert.IsType<SaleHistoryRow>(rows[1]);
        Assert.All(rows, row => Assert.True(row.CanVoid));
        Assert.Equal(
            $"1 venta · {4_000m.ToString("C", CultureInfo.CurrentCulture)} · 1 cobro · {2_500m.ToString("C", CultureInfo.CurrentCulture)}",
            SaleHistory.Summary(rows));
        Assert.Single(SaleHistory.Filter(rows, "cobro"));
    }
}
