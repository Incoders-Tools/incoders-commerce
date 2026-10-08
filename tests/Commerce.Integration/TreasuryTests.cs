using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CashSessions;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// The company's treasury beyond sales: what the POS drawer does outside a sale (withdrawals to the safe or for an
/// expense, deposits) and the cash count difference of a closed session reach the branch accounts exactly once; the
/// administration creates bank, safe and other accounts, records money in or out by hand, transfers between accounts and
/// reverses only what it recorded, all of it audited and administration only.
/// </summary>
[Collection("Postgres")]
public sealed class TreasuryTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "treasury-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource? _dataSource;

    public TreasuryTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
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
        _factory.Dispose();
    }

    private async Task<(Guid Org, Guid Branch, Guid Actor)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Treasury Org"), new NewBranch(branch, "Arrecifes"),
            new NewUserAccount(actor, $"treasury-{org}@example.com", "hash", [branch], [new RoleDto("cashier", Permission.OperatePos)]),
            CancellationToken.None));
        return (org, branch, actor);
    }

    private static SyncEnvelope Envelope(Guid org, Guid branch, Guid aggregate, string kind, string payload) => new(
        Guid.NewGuid(), 1, org, branch, aggregate, 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, payload);

    private static int _sequence = 500;

    private static SyncEnvelope CashSale(Guid org, Guid branch, decimal total)
    {
        var sale = Guid.NewGuid();
        Assert.True(SaleTenderRules.TryCash(total, total, out var cash));
        var payload = new SalePayloadV1(
            sale, total, "Scanned", DateTimeOffset.UtcNow, [], null, Tender: cash, BranchCode: 1, RegisterNumber: 2,
            SaleSequence: Interlocked.Increment(ref _sequence));
        return Envelope(org, branch, sale, SalePayloadKinds.Sale, SyncPayloadCodec.Serialize(payload));
    }

    private static SyncEnvelope Movement(Guid org, Guid branch, Guid movement, string kind, string counterpart, decimal amount) =>
        Envelope(org, branch, movement, CashMovementPayloadKinds.Recorded, SyncPayloadCodec.Serialize(new CashMovementRecordedPayloadV1(
            movement, Guid.NewGuid(), kind, counterpart, amount, "Recaudación del turno", DateTimeOffset.UtcNow, Guid.NewGuid(),
            kind == CashMovement.Withdrawal ? new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 1) : null)));

    private static SyncEnvelope Closed(Guid org, Guid branch, Guid session, decimal expected, decimal counted) =>
        Envelope(org, branch, session, CashSessionPayloadKinds.Closed, SyncPayloadCodec.Serialize(new CashSessionClosedPayloadV1(
            session, Guid.NewGuid(), 1_000m, DateTimeOffset.UtcNow, 1, expected - 1_000m, 0m, 0m, 0m, expected, counted, counted - expected)));

    private static decimal Balance(Guid branch, string kind)
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
    public async Task TheDrawerOutsideASale_ReachesTheBranchAccounts_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        inbox.TryApplyInbound(scope, CashSale(org, branch, 50_000m));

        var toSafe = Guid.NewGuid();
        inbox.TryApplyInbound(scope, Movement(org, branch, toSafe, CashMovement.Withdrawal, CashMovement.Safe, 30_000m));
        inbox.TryApplyInbound(scope, Movement(org, branch, toSafe, CashMovement.Withdrawal, CashMovement.Safe, 30_000m)); // redelivered
        inbox.TryApplyInbound(scope, Movement(org, branch, Guid.NewGuid(), CashMovement.Withdrawal, CashMovement.Expense, 4_000m));
        inbox.TryApplyInbound(scope, Movement(org, branch, Guid.NewGuid(), CashMovement.Deposit, CashMovement.Other, 1_500m));
        inbox.TryApplyInbound(scope, Movement(org, branch, Guid.NewGuid(), CashMovement.Deposit, CashMovement.Safe, 2_000m));

        Assert.Equal(50_000m - 30_000m - 4_000m + 1_500m + 2_000m, Balance(branch, PostgresTreasuryStore.Cash));
        Assert.Equal(28_000m, Balance(branch, PostgresTreasuryStore.Safe));
        using var owner = OpenOwner();
        Assert.Equal(4L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'cash-movement.recorded' AND organization_id = $1", org));
        Assert.Equal(2L, Scalar<long>(owner,
            "SELECT count(*) FROM treasury_movements WHERE organization_id = $1 AND transfer_id = $2 AND kind = 'Transfer'", org, toSafe));
    }

    [Fact]
    public async Task ACashCountDifference_IsASurplusInOrAShortageOut_OfTheDrawerAccount_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);

        var shortage = Guid.NewGuid();
        inbox.TryApplyInbound(scope, Closed(org, branch, shortage, 10_000m, 9_800m));
        inbox.TryApplyInbound(scope, Closed(org, branch, shortage, 10_000m, 9_800m)); // redelivered
        Assert.Equal(-200m, Balance(branch, PostgresTreasuryStore.Cash));

        inbox.TryApplyInbound(scope, Closed(org, branch, Guid.NewGuid(), 5_000m, 5_050m));
        inbox.TryApplyInbound(scope, Closed(org, branch, Guid.NewGuid(), 5_000m, 5_000m)); // no difference, nothing posted
        Assert.Equal(-150m, Balance(branch, PostgresTreasuryStore.Cash));

        using var owner = OpenOwner();
        Assert.Equal("Faltante de arqueo al cerrar la caja (esperado $ 10.000,00, contado $ 9.800,00)", Scalar<string>(owner,
            "SELECT concept FROM treasury_movements WHERE organization_id = $1 AND source_id = $2", org, shortage).Replace('\u00a0', ' '));
    }

    private async Task<HttpClient> SignedInAsync(Guid org, Guid branch, Permission permissions)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var user = new NewUserAccount(
                userId, email, hasher.HashPassword(new UserAccount(userId, org, [], []), Password), new[] { branch },
                new[] { new RoleDto("test-role", permissions) });
            Assert.Equal(CreateStaffUserOutcome.Created, await store.CreateStaffUserAsync(
                new CloudTenantScope(org), user,
                new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry("org-user", Guid.NewGuid(), org, "user", userId, "user.created", null, null),
                CancellationToken.None));
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branch.ToString());
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<List<JsonElement>> AccountsAsync(HttpClient admin) =>
        (await Json(await admin.GetAsync("/treasury/accounts"))).EnumerateArray().ToList();

    private static async Task<List<JsonElement>> MovementsAsync(HttpClient admin, Guid account) =>
        (await Json(await admin.GetAsync($"/treasury/accounts/{account}/movements"))).EnumerateArray().ToList();

    private static decimal BalanceOf(List<JsonElement> accounts, Guid account) =>
        accounts.Single(a => a.GetProperty("accountId").GetGuid() == account).GetProperty("balance").GetDecimal();

    [Fact]
    public async Task TheAdministration_ManagesAccounts_OfItsOwnTypes_AndRecordsMoneyByHand()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(org), CashSale(org, branch, 8_000m));
        var admin = await SignedInAsync(org, branch, Permission.ManageUsers);

        // The default types exist, and the drawer the POS created is "Efectivo".
        var cash = (await AccountsAsync(admin)).Single(a => a.GetProperty("kind").GetString() == "Cash");
        Assert.Equal("Efectivo", cash.GetProperty("accountTypeName").GetString());
        Assert.True(cash.GetProperty("isAutomatic").GetBoolean());
        var cashId = cash.GetProperty("accountId").GetGuid();
        var types = (await Json(await admin.GetAsync("/treasury/account-types"))).EnumerateArray()
            .ToDictionary(t => t.GetProperty("key").GetString()!, t => t.GetProperty("id").GetGuid());
        Assert.Equal(["bancos", "billeteras", "efectivo", "otras", "tarjetas"], types.Keys.Order());

        // The owner adds a type of its own and an account of that type.
        var credit = await admin.PostAsJsonAsync("/treasury/account-types", new { name = "Tarjetas de crédito", sortOrder = 25 });
        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        var creditType = (await Json(credit)).GetProperty("id").GetGuid();
        var visaResponse = await admin.PostAsJsonAsync("/treasury/accounts", new { name = "Visa empresa", accountTypeId = creditType });
        Assert.Equal(HttpStatusCode.Created, visaResponse.StatusCode);
        var visa = (await Json(visaResponse)).GetProperty("accountId").GetGuid();
        var bankResponse = await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Bank", name = "Banco Nación 1234/5", description = "Alias VACA.VERDE" });
        var bank = await Json(bankResponse);
        Assert.Equal("Bancos", bank.GetProperty("accountTypeName").GetString()); // its default type
        var bankId = bank.GetProperty("accountId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Safe", name = "Caja fuerte", branchId = branch })).StatusCode);
        var second = await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Safe", name = "Otra", branchId = branch });
        Assert.Equal("safe-already-exists", (await Json(second)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Safe", name = "Sin sucursal" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Cash", name = "Caja" })).StatusCode);

        // Rename and re-type an account.
        var renamed = await admin.PutAsJsonAsync($"/treasury/accounts/{visa}", new { name = "Visa Galicia", accountTypeId = creditType, description = "Cierre día 20" });
        Assert.Equal("Visa Galicia", (await Json(renamed)).GetProperty("name").GetString());

        // Money by hand and a transfer.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/treasury/movements",
            new { accountId = bankId, direction = "In", amount = 100_000m, concept = "Saldo inicial", reference = "Extracto" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/treasury/movements",
            new { accountId = bankId, direction = "In", amount = 0m, concept = "Nada" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/treasury/transfers",
            new { fromAccountId = cashId, toAccountId = bankId, amount = 5_000m, concept = "Depósito de la recaudación" })).StatusCode);
        var accounts = await AccountsAsync(admin);
        Assert.Equal((3_000m, 105_000m), (BalanceOf(accounts, cashId), BalanceOf(accounts, bankId)));

        // Deactivating: never the drawer, never with money; an inactive account takes no new movement.
        Assert.Equal("automatic-account", (await Json(await admin.PostAsJsonAsync($"/treasury/accounts/{cashId}/active", new { isActive = false }))).GetProperty("error").GetString());
        Assert.Equal("balance-not-zero", (await Json(await admin.PostAsJsonAsync($"/treasury/accounts/{bankId}/active", new { isActive = false }))).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/treasury/accounts/{visa}/active", new { isActive = false })).StatusCode);
        Assert.Equal("account-inactive", (await Json(await admin.PostAsJsonAsync("/treasury/movements",
            new { accountId = visa, direction = "In", amount = 1m, concept = "x" }))).GetProperty("error").GetString());
        Assert.False((await AccountsAsync(admin)).Single(a => a.GetProperty("accountId").GetGuid() == visa).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AVoidedMovement_IsKeptButCountsNowhere_AndAnEditReplacesIt_WhileASaleOnlyChangesAccount()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(org);
        var sale = CashSale(org, branch, 8_000m);
        inbox.TryApplyInbound(scope, sale);
        inbox.TryApplyInbound(scope, Movement(org, branch, Guid.NewGuid(), CashMovement.Withdrawal, CashMovement.Expense, 1_000m));
        var admin = await SignedInAsync(org, branch, Permission.ManageUsers);
        var cashId = (await AccountsAsync(admin)).Single(a => a.GetProperty("kind").GetString() == "Cash").GetProperty("accountId").GetGuid();
        var bankId = (await Json(await admin.PostAsJsonAsync("/treasury/accounts", new { kind = "Bank", name = "Banco" }))).GetProperty("accountId").GetGuid();
        var cardId = (await Json(await admin.PostAsJsonAsync("/treasury/accounts", new { name = "Posnet", kind = "Other" }))).GetProperty("accountId").GetGuid();
        Assert.Equal(7_000m, BalanceOf(await AccountsAsync(admin), cashId));

        // Void the POS withdrawal: it stays, voided, and the drawer gets its 1.000 back.
        var withdrawal = (await MovementsAsync(admin, cashId)).Single(m => m.GetProperty("kind").GetString() == "CashWithdrawal");
        Assert.True(withdrawal.GetProperty("canVoid").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync(
            $"/treasury/movements/{withdrawal.GetProperty("movementId").GetGuid()}/void", new { reason = "No se retiró" })).StatusCode);
        Assert.Equal(8_000m, BalanceOf(await AccountsAsync(admin), cashId));
        var voided = (await MovementsAsync(admin, cashId)).Single(m => m.GetProperty("kind").GetString() == "CashWithdrawal");
        Assert.True(voided.GetProperty("voided").GetBoolean());
        Assert.Equal("No se retiró", voided.GetProperty("voidReason").GetString());
        Assert.Equal("not-voidable", (await Json(await admin.PostAsJsonAsync(
            $"/treasury/movements/{withdrawal.GetProperty("movementId").GetGuid()}/void", new { reason = "otra vez" }))).GetProperty("error").GetString());

        // Edit a transfer: both legs are replaced, the balances follow.
        await admin.PostAsJsonAsync("/treasury/transfers", new { fromAccountId = cashId, toAccountId = bankId, amount = 5_000m, concept = "Depósito" });
        var leg = (await MovementsAsync(admin, bankId)).Single();
        var edited = await admin.PutAsJsonAsync($"/treasury/movements/{leg.GetProperty("movementId").GetGuid()}",
            new { amount = 4_500m, reason = "La boleta decía 4.500" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var accounts = await AccountsAsync(admin);
        Assert.Equal((3_500m, 4_500m), (BalanceOf(accounts, cashId), BalanceOf(accounts, bankId)));
        var bankMovements = await MovementsAsync(admin, bankId);
        var replacement = bankMovements.Single(m => !m.GetProperty("voided").GetBoolean());
        Assert.Equal("Transferencia desde Caja efectivo · Arrecifes: Depósito", replacement.GetProperty("concept").GetString());
        Assert.Equal(leg.GetProperty("movementId").GetGuid(), replacement.GetProperty("correctsMovementId").GetGuid());
        Assert.Equal("Editado: La boleta decía 4.500", bankMovements.Single(m => m.GetProperty("voided").GetBoolean()).GetProperty("voidReason").GetString());

        // A sale is not voided here, it only changes account; voiding it at the POS then takes the money from where it is now.
        var saleMovement = (await MovementsAsync(admin, cashId)).Single(m => m.GetProperty("kind").GetString() == "Sale");
        Assert.False(saleMovement.GetProperty("canVoid").GetBoolean());
        Assert.True(saleMovement.GetProperty("canReclassify").GetBoolean());
        var saleId = saleMovement.GetProperty("movementId").GetGuid();
        Assert.Equal("not-voidable", (await Json(await admin.PostAsJsonAsync($"/treasury/movements/{saleId}/void", new { reason = "x" }))).GetProperty("error").GetString());
        Assert.Equal("only-account-editable", (await Json(await admin.PutAsJsonAsync($"/treasury/movements/{saleId}",
            new { amount = 1m, reason = "x" }))).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/treasury/movements/{saleId}",
            new { accountId = cardId, reason = "Se cobró con tarjeta" })).StatusCode);
        accounts = await AccountsAsync(admin);
        Assert.Equal((-4_500m, 8_000m), (BalanceOf(accounts, cashId), BalanceOf(accounts, cardId)));

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(sale.Payload);
        inbox.TryApplyInbound(scope, Envelope(org, branch, payload.SaleId, SalePayloadKinds.Voided, SyncPayloadCodec.Serialize(
            new SaleVoidedPayloadV1(payload.SaleId, DateTimeOffset.UtcNow, Guid.NewGuid(),
                new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 1), "Error", 8_000m, payload.Tender, null))));
        accounts = await AccountsAsync(admin);
        Assert.Equal((-4_500m, 0m), (BalanceOf(accounts, cashId), BalanceOf(accounts, cardId)));

        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE organization_id = $1 AND action = 'treasury.movement_voided'", org));
        Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE organization_id = $1 AND action = 'treasury.movement_edited'", org));
        Assert.Equal(2L, Scalar<long>(owner,
            "SELECT count(*) FROM audit_log WHERE organization_id = $1 AND action = 'treasury.movement_edited' AND old_value IS NOT NULL", org));
    }

    [Fact]
    public async Task Reprocessing_BringsInTheOperationsReceivedBeforeTheTreasury_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        // Two sales and a withdrawal the cloud received while it had no treasury: in the inbox, without money postings.
        var envelopes = new[]
        {
            CashSale(org, branch, 5_000m),
            CashSale(org, branch, 2_000m),
            Movement(org, branch, Guid.NewGuid(), CashMovement.Withdrawal, CashMovement.Expense, 1_000m),
        };
        using (var owner = OpenOwner())
        {
            foreach (var envelope in envelopes)
            {
                using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO sync_inbox (operation_id, organization_id, branch_id, aggregate_id, aggregate_version, actor_id,
                                            correlation_id, occurred_at_utc, payload_kind, payload, status)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb, 'Applied')
                    """, owner);
                foreach (var value in new object[]
                {
                    envelope.OperationId, envelope.OrganizationId, envelope.BranchId, envelope.AggregateId, envelope.AggregateVersion,
                    envelope.ActorId, envelope.CorrelationId, envelope.OccurredAtUtc, envelope.PayloadKind, envelope.Payload,
                })
                {
                    insert.Parameters.AddWithValue(value);
                }

                insert.ExecuteNonQuery();
            }
        }

        var admin = await SignedInAsync(org, branch, Permission.ManageUsers);
        var first = await Json(await admin.PostAsJsonAsync("/treasury/reprocess", new { }));
        Assert.Equal(3, first.GetProperty("movementsAdded").GetInt32());
        Assert.Equal(6_000m, Balance(branch, PostgresTreasuryStore.Cash));

        var again = await Json(await admin.PostAsJsonAsync("/treasury/reprocess", new { }));
        Assert.Equal(0, again.GetProperty("movementsAdded").GetInt32()); // never twice
        Assert.Equal(6_000m, Balance(branch, PostgresTreasuryStore.Cash));
    }

    [Fact]
    public async Task TheTreasury_IsAdministrationOnly()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        var seller = await SignedInAsync(org, branch, Permission.ViewSales | Permission.TakeOrders);

        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync("/treasury/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/treasury/accounts", new { kind = "Bank", name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/treasury/transfers",
            new { fromAccountId = Guid.NewGuid(), toAccountId = Guid.NewGuid(), amount = 1m, concept = "x" })).StatusCode);

        // The staff and their payroll too.
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync("/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync("/payroll/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/payroll/runs",
            new { periodFrom = "2026-10-01", periodTo = "2026-10-31" })).StatusCode);
    }
}
