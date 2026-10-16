using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.BranchNode;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Customers;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// Customer current accounts: the customer side of the ledger rules (what the customer owes: a sale on account is a
/// Debit, a payment a Credit; aging works the same as for suppliers), a POS sale on current account (never counted as
/// cash, always with a customer) charged to the customer in the cloud exactly once, and its charge reversed when the
/// sale is voided.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerAccountTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"branch-account-sale-{Guid.NewGuid():N}.db");

    public CustomerAccountTests()
    {
        if (!_postgresAvailable) return;
        using (var owner = OpenOwner())
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
            {
                PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
            }
        }
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private static AccountMovementFact Fact(AccountMovementKind kind, AccountDirection direction, decimal amount, DateOnly on, DateOnly? due = null) =>
        new(Guid.NewGuid(), kind, direction, amount, on, due, null);

    [Fact]
    public void ACustomerOwesWhatItBoughtOnAccount_LessWhatItPaid_AndItsOverdueDebtAgesFifo()
    {
        Assert.True(PartyAccountRules.TryResolveDirection(AccountPartyKind.Customer, AccountMovementKind.Invoice, null, out var sale, out _));
        Assert.True(PartyAccountRules.TryResolveDirection(AccountPartyKind.Customer, AccountMovementKind.Payment, null, out var payment, out _));
        Assert.Equal((AccountDirection.Debit, AccountDirection.Credit), (sale, payment));
        Assert.False(PartyAccountRules.TryResolveDirection(
            AccountPartyKind.Customer, AccountMovementKind.Invoice, AccountDirection.Credit, out _, out var error));
        Assert.Equal("direction of a Invoice is always Debit.", error);

        var movements = new[]
        {
            Fact(AccountMovementKind.Invoice, AccountDirection.Debit, 1_000m, Today.AddDays(-70), Today.AddDays(-40)),
            Fact(AccountMovementKind.Invoice, AccountDirection.Debit, 500m, Today.AddDays(-5), Today.AddDays(10)),
            Fact(AccountMovementKind.Payment, AccountDirection.Credit, 300m, Today.AddDays(-2)),
        };
        var summary = PartyAccountRules.Summarize(AccountPartyKind.Customer, movements, Today);

        Assert.Equal(1_200m, summary.Balance);      // owes 1.500 - 300
        Assert.Equal(700m, summary.Overdue);       // the payment went to the oldest debt first
        Assert.Equal(700m, summary.Aging.D31To60);
        Assert.Equal(500m, summary.Current);
        Assert.Equal(1_200m, PartyAccountRules.Balance(AccountPartyKind.Customer, movements));
        Assert.Equal(-1_200m, PartyAccountRules.Balance(AccountPartyKind.Supplier, movements)); // the supplier rules read it the other way
    }

    [Fact]
    public void AtThePos_ASaleOnAccount_NeedsItsCustomer_AndIsNotCashInTheDrawer()
    {
        using var store = new BranchSyncStore($"Data Source={_dbPath}");
        var sink = new InMemoryAuditSink();
        var service = new BranchNodeService(store, new TenantAuthorizationService(sink), sink);
        var (org, branch, cashier) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var session = service.OpenCashSession(org, branch, cashier, 1_000m, Guid.NewGuid()).Session!.SessionId;
        SaleLine[] Line(Guid sale) => [new SaleLine(sale, 1, Guid.NewGuid(), null, "Vacío", "Por kg", 1m, 9_000m, 9_000m)];

        var walkIn = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => service.CompleteScannedSale(
            org, branch, cashier, walkIn, Line(walkIn), 9_000m, Guid.NewGuid(), Guid.NewGuid(), tender: SaleTenderRules.Account()));

        var onAccount = Guid.NewGuid();
        service.CompleteScannedSale(
            org, branch, cashier, onAccount, Line(onAccount), 9_000m, Guid.NewGuid(), Guid.NewGuid(), customerId: Guid.NewGuid(),
            tender: SaleTenderRules.Account());
        var card = Guid.NewGuid();
        service.CompleteScannedSale(org, branch, cashier, card, Line(card), 9_000m, Guid.NewGuid(), Guid.NewGuid(), tender: SaleTenderRules.Card());

        var summary = service.GetCashSessionSummary(session)!;
        Assert.Equal((2, 9_000m, 9_000m, 1_000m), (summary.SaleCount, summary.AccountTotal, summary.CardTotal, summary.ExpectedCash));

        var closed = service.CloseCashSession(session, cashier, 1_000m, Guid.NewGuid());
        Assert.Equal(9_000m, closed.Session!.Closure!.Summary.AccountTotal);
        Assert.Equal(9_000m, service.GetCashSession(session)!.Closure!.Summary.AccountTotal); // stored, not only computed
        var payload = SyncPayloadCodec.Deserialize<CashSessionClosedPayloadV1>(
            store.GetPendingOutbox(branch).Single(e => e.PayloadKind == CashSessionPayloadKinds.Closed).Payload);
        Assert.Equal((9_000m, 0m), (payload.AccountTotal, payload.Difference));
    }

    private async Task<(Guid Org, Guid Branch, Guid Customer)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Account Org"), new NewBranch(branch, "Main"),
            new NewUserAccount(actor, $"acct-{org}@example.com", "hash", [branch], [new RoleDto("cashier", Permission.OperatePos)]),
            CancellationToken.None));
        var customer = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(new CloudTenantScope(org),
            new NewCustomer(customer, CustomerKind.Wholesale, "Parrilla Don Julio", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal,
                null, null, null, null, null, null, null, null, null, null, null, null, actor),
            "org-user", actor, CancellationToken.None);
        return (org, branch, customer);
    }

    private static SyncEnvelope SaleOnAccount(Guid org, Guid branch, Guid sale, Guid? customer, decimal total, string method = SaleTender.Account)
    {
        var payload = new SalePayloadV1(
            sale, total, "Scanned", new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeSpan.Zero), [], customer,
            Tender: new SaleTender(method), BranchCode: 1, RegisterNumber: 2, SaleSequence: 125);
        return new SyncEnvelope(
            Guid.NewGuid(), 1, org, branch, sale, 1, Guid.NewGuid(), Guid.NewGuid(), payload.OccurredAtUtc, SalePayloadKinds.Sale,
            SyncPayloadCodec.Serialize(payload));
    }

    private static SyncEnvelope Void(Guid org, Guid branch, Guid sale, decimal total)
    {
        var payload = new SaleVoidedPayloadV1(
            sale, new DateTimeOffset(2026, 10, 5, 16, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
            new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 1), "Se equivocó de cliente", total,
            SaleTenderRules.Account(), null);
        return new SyncEnvelope(
            Guid.NewGuid(), 1, org, branch, sale, 2, payload.VoidedByOperatorId, Guid.NewGuid(), payload.VoidedAtUtc,
            SalePayloadKinds.Voided, SyncPayloadCodec.Serialize(payload));
    }

    private async Task<IReadOnlyList<AccountMovementRecord>> MovementsAsync(Guid org, Guid customer) =>
        (await new PostgresCurrentAccountStore(_dataSource!).ListMovementsAsync(new CloudTenantScope(org), AccountParty.Customer(customer), default))!;

    [Fact]
    public async Task ASaleOnAccount_IsChargedToItsCustomerOnce_AndItsVoidTakesTheChargeBack()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var sale = Guid.NewGuid();

        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, sale, customer, 18_000m));
        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, sale, customer, 18_000m)); // the same sale, another operation id

        var charge = Assert.Single(await MovementsAsync(org, customer));
        Assert.Equal((AccountMovementKind.Invoice, AccountDirection.Debit, 18_000m, "V01-C2-125", new DateOnly(2026, 10, 5)),
            (charge.Kind, charge.Direction, charge.Amount, charge.DocumentReference, charge.OccurredOn));
        Assert.Equal(new DateOnly(2026, 11, 4), charge.DueOn); // the organization's default: 30 days
        Assert.Contains("plazo general", charge.Concept);
        Assert.Equal(customer, charge.CustomerId);
        Assert.Null(charge.SupplierId);

        var voidEnvelope = Void(org, branch, sale, 18_000m);
        inbox.TryApplyInbound(scope, voidEnvelope);
        inbox.TryApplyInbound(scope, voidEnvelope);

        var movements = await MovementsAsync(org, customer);
        Assert.Equal(2, movements.Count);
        var reversal = movements.Single(m => m.Kind == AccountMovementKind.Reversal);
        Assert.Equal((AccountDirection.Credit, charge.Id), (reversal.Direction, reversal.ReversesMovementId));
        Assert.Contains("Se equivocó de cliente", reversal.Concept);
        Assert.Equal(0m, PartyAccountRules.Balance(AccountPartyKind.Customer, movements.Select(m => m.ToFact())));
    }

    [Fact]
    public async Task ASaleOnAccountVoidedBeforeItArrives_IsNeverCharged()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var sale = Guid.NewGuid();

        inbox.TryApplyInbound(scope, Void(org, branch, sale, 7_000m));
        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, sale, customer, 7_000m));

        Assert.Empty(await MovementsAsync(org, customer));
    }

    [Fact]
    public async Task ThePaymentsRegisteredOnACustomerAccount_LowerWhatItOwes_AndTheBalancesListIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var store = new PostgresCurrentAccountStore(_dataSource!);
        var scope = new CloudTenantScope(org, BranchId: branch);
        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(org), SaleOnAccount(org, branch, Guid.NewGuid(), customer, 10_000m));

        var payment = await store.RegisterAsync(scope, AccountParty.Customer(customer),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Payment, AccountDirection.Credit, 4_000m, Today, null, "Recibo 12", "Cobro en efectivo", Guid.NewGuid()),
            "org-user", Guid.NewGuid(), default);
        Assert.NotNull(payment);
        Assert.Null(await store.RegisterAsync(scope, AccountParty.Customer(Guid.NewGuid()),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Payment, AccountDirection.Credit, 1m, Today, null, null, "x", Guid.NewGuid()),
            "org-user", Guid.NewGuid(), default));

        var balance = Assert.Single(await store.CustomerBalancesAsync(scope, Today, default));
        Assert.Equal((customer, 6_000m), (balance.CustomerId, balance.Balance));
        Assert.DoesNotContain(await store.BalancesAsync(scope, Today, default), b => b.SupplierId == customer);
    }

    private static decimal Treasury(Guid branch, string kind)
    {
        using var owner = OpenOwner();
        return Scalar<decimal>(owner,
            """
            SELECT COALESCE(SUM(CASE WHEN m.direction = 'In' THEN m.amount ELSE -m.amount END), 0)
            FROM treasury_movements m JOIN treasury_accounts a ON a.organization_id = m.organization_id AND a.id = m.account_id
            WHERE a.branch_id = $1 AND a.kind = $2
            """, branch, kind);
    }

    [Fact]
    public async Task EverySale_PutsItsMoneyInTheBranchTreasury_AndASaleToACustomerShowsInItsAccount_UntilItIsVoided()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var cashToCustomer = Guid.NewGuid();
        var cardWalkIn = Guid.NewGuid();

        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, cashToCustomer, customer, 12_000m, SaleTender.Cash));
        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, cashToCustomer, customer, 12_000m, SaleTender.Cash)); // redelivered
        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, cardWalkIn, null, 3_000m, SaleTender.Card));

        Assert.Equal((12_000m, 3_000m, 0m), (Treasury(branch, "Cash"), Treasury(branch, "Card"), Treasury(branch, "Qr")));
        var movements = await MovementsAsync(org, customer);
        Assert.Equal([AccountMovementKind.Invoice, AccountMovementKind.Payment], movements.Select(m => m.Kind));
        Assert.Equal(0m, PartyAccountRules.Balance(AccountPartyKind.Customer, movements.Select(m => m.ToFact()))); // paid at the counter
        using (var owner = OpenOwner())
        {
            Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'pos-sale.recorded' AND organization_id = $1", org));
        }

        inbox.TryApplyInbound(scope, Void(org, branch, cashToCustomer, 12_000m));

        Assert.Equal(0m, Treasury(branch, "Cash"));
        var afterVoid = await MovementsAsync(org, customer);
        Assert.Equal(2, afterVoid.Count(m => m.Kind == AccountMovementKind.Reversal));
        Assert.Equal(0m, PartyAccountRules.Balance(AccountPartyKind.Customer, afterVoid.Select(m => m.ToFact())));
    }

    [Fact]
    public async Task ASaleOnAccount_IsDueAfterTheCustomersOwnTerms_WhenItHasThem()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        using (var owner = OpenOwner())
        {
            Exec(owner, "UPDATE customers SET payment_terms_days = 15 WHERE id = $1", customer);
        }

        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(org), SaleOnAccount(org, branch, Guid.NewGuid(), customer, 1_000m));

        var charge = Assert.Single(await MovementsAsync(org, customer));
        Assert.Equal(new DateOnly(2026, 10, 20), charge.DueOn);
        Assert.Contains("plazo del cliente", charge.Concept);
        Assert.Equal(0m, Treasury(branch, "Cash")); // on account: no money moved
    }

    private static SyncEnvelope PaymentReceived(Guid org, Guid branch, Guid payment, Guid customer, decimal amount, SaleTender tender)
    {
        var payload = new CustomerPaymentReceivedPayloadV1(
            payment, customer, amount, tender, new DateTimeOffset(2026, 10, 6, 14, 0, 0, TimeSpan.Zero), Guid.NewGuid(), "Pago parcial");
        return new SyncEnvelope(
            Guid.NewGuid(), 1, org, branch, payment, 1, Guid.NewGuid(), Guid.NewGuid(), payload.ReceivedAtUtc,
            CustomerPaymentPayloadKinds.Received, SyncPayloadCodec.Serialize(payload));
    }

    private static SyncEnvelope PaymentVoided(Guid org, Guid branch, Guid payment, Guid customer, decimal amount, SaleTender tender)
    {
        var payload = new CustomerPaymentVoidedPayloadV1(
            payment, customer, amount, tender, new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
            new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 1), "Se cobró dos veces", null);
        return new SyncEnvelope(
            Guid.NewGuid(), 1, org, branch, payment, 2, payload.VoidedByOperatorId, Guid.NewGuid(), payload.VoidedAtUtc,
            CustomerPaymentPayloadKinds.Voided, SyncPayloadCodec.Serialize(payload));
    }

    [Fact]
    public async Task APaymentCollectedAtThePos_LowersWhatTheCustomerOwes_PutsTheMoneyInTheTreasury_Once_AndItsVoidTakesItBack()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        inbox.TryApplyInbound(scope, SaleOnAccount(org, branch, Guid.NewGuid(), customer, 20_000m));
        var payment = Guid.NewGuid();
        Assert.True(SaleTenderRules.TryCash(8_000m, 10_000m, out var cash));

        inbox.TryApplyInbound(scope, PaymentReceived(org, branch, payment, customer, 8_000m, cash));
        inbox.TryApplyInbound(scope, PaymentReceived(org, branch, payment, customer, 8_000m, cash)); // the same payment, another operation

        var movements = await MovementsAsync(org, customer);
        Assert.Equal(12_000m, PartyAccountRules.Balance(AccountPartyKind.Customer, movements.Select(m => m.ToFact())));
        Assert.Equal(8_000m, Treasury(branch, "Cash"));
        Assert.Contains(movements, m => m.Kind == AccountMovementKind.Payment && m.Concept.Contains("Pago parcial"));
        using (var owner = OpenOwner())
        {
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'customer-payment.received' AND entity_id = $1", customer));
        }

        inbox.TryApplyInbound(scope, PaymentVoided(org, branch, payment, customer, 8_000m, cash));

        Assert.Equal(20_000m, PartyAccountRules.Balance(AccountPartyKind.Customer, (await MovementsAsync(org, customer)).Select(m => m.ToFact())));
        Assert.Equal(0m, Treasury(branch, "Cash"));
        using (var owner = OpenOwner())
        {
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'customer-payment.voided' AND entity_id = $1", customer));
        }
    }

    [Fact]
    public async Task APaymentVoidedBeforeItArrives_IsNeverPosted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, customer) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var payment = Guid.NewGuid();

        inbox.TryApplyInbound(scope, PaymentVoided(org, branch, payment, customer, 500m, SaleTenderRules.Qr()));
        inbox.TryApplyInbound(scope, PaymentReceived(org, branch, payment, customer, 500m, SaleTenderRules.Qr()));

        Assert.Empty(await MovementsAsync(org, customer));
        Assert.Equal(0m, Treasury(branch, "Qr"));
    }
}
