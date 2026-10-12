using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Commerce.Domain.Payroll;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// employee-lifecycle E4: the goods an employee takes, item by item, whatever the channel. The web registers them like a
/// sale, priced on the default list (Mostrador, never the linked customer's own list): stock out, a debt on the
/// employee's linked customer account, and its items pending for the payroll. A POS sale on current account to that
/// customer leaves its items too (the POS already moved the stock and the debt), with a sale discount spread over them so
/// they add up to what was charged; redelivered it adds nothing, voided it takes them back.
/// </summary>
public sealed partial class PayrollTests
{
    private const string GoodsMigration = "0056_employee_goods.sql";

    private sealed record GoodsWorld(World W, EmployeeRecord Juan, Guid Asado, Guid Chorizo);

    /// <summary>Juan takes goods; Asado (kg) is 10.000 on Mostrador and 8.000 on Reparto (his customer's list); Chorizo 4.500.</summary>
    private async Task<GoodsWorld> GoodsSeedAsync()
    {
        var w = await SeedAsync();
        var (_, juan) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: true, CancellationToken.None);
        var asado = Presentation(w.Org, w.Branch, "Asado", "Por kg");
        var chorizo = Presentation(w.Org, w.Branch, "Chorizo", "Por kg");
        using var owner = OpenOwner();
        var mostrador = Scalar<Guid?>(owner, "SELECT id FROM price_lists WHERE organization_id = $1 AND is_default", w.Org);
        if (mostrador is null)
        {
            mostrador = Guid.NewGuid();
            Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Mostrador', true, $4)",
                mostrador.Value, w.Org, w.Branch, w.Actor);
        }

        var reparto = Guid.NewGuid();
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Reparto', false, $4)",
            reparto, w.Org, w.Branch, w.Actor);
        void Price(Guid list, Guid presentation, decimal price) => Exec(owner,
            "INSERT INTO price_list_entries (id, organization_id, branch_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)",
            Guid.NewGuid(), w.Org, w.Branch, list, presentation, price, new DateOnly(2026, 1, 1), w.Actor);
        Price(mostrador.Value, asado, 10_000m);
        Price(reparto, asado, 8_000m);
        Price(mostrador.Value, chorizo, 4_500m);
        Exec(owner, "UPDATE customers SET price_list_id = $2 WHERE id = $1", juan!.CustomerId!.Value, reparto);
        return new GoodsWorld(w, juan, asado, chorizo);
    }

    private PostgresEmployeeGoodsStore Goods() => new(_dataSource!, new PostgresPriceListStore(_dataSource!), new PostgresRateComponentStore(_dataSource!));

    [Fact]
    public void GoodsMigration_RerunsSafely_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, GoodsMigration);
        PostgresTestFixture.ApplyMigration(owner, GoodsMigration);
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'employee_goods_items'"));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", GoodsMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public void SpreadingASaleDiscount_KeepsTheItemsAddingUpToWhatWasCharged()
    {
        Assert.Equal([9_000m, 4_500m], EmployeeGoodsRules.Spread([10_000m, 5_000m], 13_500m));
        Assert.Equal([3_333.33m, 3_333.33m, 3_333.34m], EmployeeGoodsRules.Spread([5_000m, 5_000m, 5_000m], 10_000m));
        Assert.Equal([7_000m], EmployeeGoodsRules.Spread([7_000m], 7_000m));
        Assert.True(EmployeeGoodsRules.IsValidQuantity(1.5m));
        Assert.False(EmployeeGoodsRules.IsValidQuantity(0m));
        Assert.False(EmployeeGoodsRules.IsValidQuantity(1.2345m));
    }

    [Fact]
    public async Task TheWeb_RegistersGoodsLikeASale_AtTheMostradorPrice_OutOfStock_OnTheEmployeesAccount()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        GoodsLineInput[] lines = [new(g.Asado, 1.5m), new(g.Chorizo, 2m)];

        // The quote: Mostrador's prices even though Juan's customer is on Reparto.
        var (quoted, quote) = await goods.QuoteAsync(g.W.Scope, g.Juan.Id, lines, CancellationToken.None);
        Assert.Equal(EmployeeGoodsOutcome.Done, quoted);
        Assert.Equal([(10_000m, 15_000m), (4_500m, 9_000m)], quote!.Lines.Select(l => (l.UnitPrice, l.LineTotal)));
        Assert.Equal(24_000m, quote.Total);

        var (registered, record) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), lines, CancellationToken.None);
        Assert.Equal(EmployeeGoodsOutcome.Done, registered);
        Assert.Equal(24_000m, record!.Total);

        // The debt on his goods account (what the payroll deducts), the stock out, and the items pending.
        Assert.Equal(24_000m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
        using var owner = OpenOwner();
        Assert.Equal(-1.5m, Scalar<decimal>(owner,
            "SELECT quantity FROM stock_movements WHERE presentation_id = $1 AND kind = 'Sale' AND source_type = 'EmployeeGoods'", g.Asado));
        var items = await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None);
        Assert.Equal([("Asado", 1.5m, 15_000m, OvertimeStatus.Pending), ("Chorizo", 2m, 9_000m, OvertimeStatus.Pending)],
            items.Select(i => (i.ProductName, i.Quantity, i.LineTotal, i.Status)).OrderBy(i => i.Item1));
        Assert.All(items, i => Assert.Equal("Web", i.SourceType));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.goods_registered' AND entity_id = $1", g.Juan.Id));
    }

    [Fact]
    public async Task RegisteringGoods_IsRefused_WithoutAGoodsAccount_OrWithoutAPrice()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var (_, ana) = await Employees().CreateAsync(g.W.Scope, g.W.Actor, Input(g.W, "Ana", 300_000m), takesGoods: false, CancellationToken.None);
        var unpriced = Presentation(g.W.Org, g.W.Branch, "Lengua", "Por kg");

        Assert.Equal(EmployeeGoodsOutcome.NoGoodsAccount,
            (await goods.RegisterAsync(g.W.Scope, g.W.Actor, ana!.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None)).Outcome);
        Assert.Equal(EmployeeGoodsOutcome.NoPrice,
            (await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(unpriced, 1m)], CancellationToken.None)).Outcome);
        Assert.Equal(EmployeeGoodsOutcome.NotFound,
            (await goods.RegisterAsync(g.W.Scope, g.W.Actor, Guid.NewGuid(), new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None)).Outcome);
        Assert.Empty(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        Assert.Equal(0m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
    }

    [Fact]
    public async Task APosSaleOnAccount_ToTheEmployee_LeavesItsItems_WithTheSaleDiscountSpread_OnceAndUntilVoided()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var inbox = new PostgresCloudInboxStore(_dataSource!);
        var scope = new CloudTenantScope(g.W.Org);
        var sale = Guid.NewGuid();
        SaleLine[] lines =
        [
            new(sale, 1, g.Asado, null, "Asado", "Por kg", 1m, 10_000m, 10_000m),
            new(sale, 2, g.Chorizo, null, "Chorizo", "Por kg", 1m, 5_000m, 5_000m),
        ];
        var onAccount = PosSale(g.W, sale, g.Juan.CustomerId, lines, total: 13_500m, discountPercent: 10m);

        inbox.TryApplyInbound(scope, onAccount);
        inbox.TryApplyInbound(scope, onAccount with { OperationId = Guid.NewGuid() }); // redelivered
        var items = await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None);
        Assert.Equal([("Asado", 9_000m), ("Chorizo", 4_500m)], items.Select(i => (i.ProductName, i.LineTotal)).OrderBy(i => i.Item1));
        Assert.All(items, i => Assert.Equal("PosSale", i.SourceType));

        // A sale to a customer who is no employee leaves nothing; nor does a sale paid at the counter.
        var stranger = await new PostgresCustomerStore(_dataSource!).CreateAsync(g.W.Scope, new NewCustomer(
            Guid.NewGuid(), Commerce.Domain.Customers.CustomerKind.Retail, "Otro cliente", null, Commerce.Domain.Customers.TaxIdType.None, null,
            Commerce.Domain.Customers.TaxCondition.ConsumidorFinal, null, null, null, null, null, null, null, null, null, null, null, null, g.W.Actor),
            "org-user", g.W.Actor, CancellationToken.None);
        inbox.TryApplyInbound(scope, PosSale(g.W, Guid.NewGuid(), stranger.Id, lines, total: 15_000m));
        inbox.TryApplyInbound(scope, PosSale(g.W, Guid.NewGuid(), g.Juan.CustomerId, lines, total: 15_000m, method: SaleTender.Cash));
        Assert.Equal(2, (await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None)).Count);
        using (var owner = OpenOwner())
        {
            Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM employee_goods_items WHERE organization_id = $1", g.W.Org));
        }

        // Voided, its items go.
        inbox.TryApplyInbound(scope, PosSaleVoid(g.W, sale, 13_500m));
        Assert.Empty(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
    }

    private static SyncEnvelope PosSale(
        World w, Guid sale, Guid? customer, SaleLine[] lines, decimal total, decimal? discountPercent = null, string method = SaleTender.Account)
    {
        var payload = new SalePayloadV1(
            sale, total, "Scanned", new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero), [.. lines.Select(l => l with { SaleId = sale })],
            customer, SaleDiscountPercent: discountPercent, SaleDiscountAmount: discountPercent is null ? null : lines.Sum(l => l.LineTotal) - total,
            Tender: new SaleTender(method), BranchCode: 1, RegisterNumber: 2, SaleSequence: Random.Shared.Next(1, 1_000_000));
        return new SyncEnvelope(
            Guid.NewGuid(), 1, w.Org, w.Branch, sale, 1, w.Actor, Guid.NewGuid(), payload.OccurredAtUtc, SalePayloadKinds.Sale,
            SyncPayloadCodec.Serialize(payload));
    }

    private static SyncEnvelope PosSaleVoid(World w, Guid sale, decimal total)
    {
        var payload = new SaleVoidedPayloadV1(
            sale, new DateTimeOffset(2026, 10, 12, 16, 0, 0, TimeSpan.Zero), Guid.NewGuid(),
            new DiscountAuthorization(DiscountAuthorization.BranchPin, Guid.NewGuid(), 1), "Se equivocó", total, SaleTenderRules.Account(), null);
        return new SyncEnvelope(
            Guid.NewGuid(), 1, w.Org, w.Branch, sale, 2, payload.VoidedByOperatorId, Guid.NewGuid(), payload.VoidedAtUtc,
            SalePayloadKinds.Voided, SyncPayloadCodec.Serialize(payload));
    }
}
