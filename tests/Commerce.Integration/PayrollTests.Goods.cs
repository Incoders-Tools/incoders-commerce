using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
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

    [Fact]
    public async Task RegisteringGoodsTwiceWithTheSameKey_MovesTheStockAndTheDebtOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var key = Guid.NewGuid();
        GoodsLineInput[] lines = [new(g.Asado, 1m)];

        // A retry after a lost answer (the web sends the same key) gets the first registration back.
        var (_, first) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), lines, CancellationToken.None, key);
        var (again, second) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), lines, CancellationToken.None, key);
        Assert.Equal(EmployeeGoodsOutcome.Done, again);
        Assert.Equal((key, 10_000m), (first!.GoodsId, second!.Total));

        Assert.Equal(10_000m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
        Assert.Single(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE source_type = 'EmployeeGoods' AND source_id = $1", key));

        // The key belongs to Juan's registration: another employee with it is refused, never handed Juan's goods.
        var (_, ana) = await Employees().CreateAsync(g.W.Scope, g.W.Actor, Input(g.W, "Ana", 300_000m), takesGoods: true, CancellationToken.None);
        Assert.Equal(EmployeeGoodsOutcome.KeyInUse,
            (await goods.RegisterAsync(g.W.Scope, g.W.Actor, ana!.Id, new DateOnly(2026, 10, 12), lines, CancellationToken.None, key)).Outcome);
        Assert.Empty(await goods.ListAsync(g.W.Scope, ana.Id, CancellationToken.None));
    }

    [Fact]
    public async Task APosSaleWithAReturnedLine_IsStillCharged_AndItsTakenLinesAddUpToTheCharge()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var sale = Guid.NewGuid();
        SaleLine[] lines =
        [
            new(sale, 1, g.Asado, null, "Asado", "Por kg", 1m, 10_000m, 10_000m),
            new(sale, 2, g.Chorizo, null, "Chorizo", "Por kg", -1m, 5_000m, -5_000m),
        ];
        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(g.W.Org), PosSale(g.W, sale, g.Juan.CustomerId, lines, total: 5_000m));

        // The goods never hold the money back: the sale is charged whatever its items are.
        Assert.Equal(5_000m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
        var item = Assert.Single(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        Assert.Equal(("Asado", 5_000m), (item.ProductName, item.LineTotal));
    }

    [Fact]
    public void TheGoodsDiscount_IsPerItemFirst_ThenThePercentOnTheRest_AndNeverMoreThanWhatIsOwed()
    {
        Assert.Equal(1_875m, EmployeeGoodsRules.ItemDiscount(15_000m, 12.5m));
        Assert.Equal(0m, EmployeeGoodsRules.ItemDiscount(15_000m, 0m));

        // 30.000 owed, 5.000 off its items, then 10 % off the remaining 25.000.
        var purchases = new PayslipPurchases(30_000m, 10m, 5_000m);
        Assert.Equal((7_500m, 22_500m), (purchases.Discount, purchases.Deducted));

        // Without item discounts it is the percent on the whole, as before; items never forgive more than is owed.
        Assert.Equal((5_000m, 45_000m), (new PayslipPurchases(50_000m, 10m).Discount, new PayslipPurchases(50_000m, 10m).Deducted));
        var capped = new PayslipPurchases(1_000m, 50m, 3_000m);
        Assert.Equal((1_000m, 0m), (capped.Discount, capped.Deducted));
    }

    [Fact]
    public async Task APayroll_TakesThePendingGoods_DiscountsThemPerItemAndOnTheTotal_AndPaysThemOff()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var payroll = new PostgresPayrollStore(_dataSource!);
        await Goods().RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1.5m), new(g.Chorizo, 2m)],
            CancellationToken.None);

        // A debt from before the items were kept (6.000) is goods too: it only gets the discount on the total.
        await new PostgresCurrentAccountStore(_dataSource!).RegisterAsync(g.W.Scope, AccountParty.Customer(g.Juan.CustomerId!.Value),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Invoice, AccountDirection.Debit, 6_000m, new DateOnly(2026, 9, 30), null, null,
                "Saldo anterior", g.W.Actor), "org-user", g.W.Actor, CancellationToken.None);

        var (_, runId) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, CancellationToken.None);
        var slip = Assert.Single((await payroll.GetRunAsync(g.W.Scope, runId!.Value, CancellationToken.None))!.Payslips);
        Assert.Equal(30_000m, slip.PurchasesAmount);
        Assert.Equal([("Asado", 15_000m), ("Chorizo", 9_000m)], slip.GoodsItems.Select(i => (i.ProductName, i.LineTotal)).OrderBy(i => i.Item1));
        Assert.All(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None), i => Assert.Equal((OvertimeStatus.InPayroll, 1), (i.Status, i.RunNumber)));

        // 20 % off the asado (3.000), then 10 % off the remaining 27.000 (2.700).
        var asado = slip.GoodsItems.Single(i => i.ProductName == "Asado").Id;
        PayslipLineInput[] salary = [new(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 500_000m)];
        Assert.Equal(PayrollWriteOutcome.GoodsItemNotInPayslip, await payroll.UpdatePayslipAsync(g.W.Scope, g.W.Actor, runId.Value, slip.PayslipId,
            salary, 10m, CancellationToken.None, [new GoodsItemDiscountInput(asado, 20m), new GoodsItemDiscountInput(Guid.NewGuid(), 5m)]));
        Assert.Equal(0m, (await payroll.GetRunAsync(g.W.Scope, runId.Value, CancellationToken.None))!.Payslips.Single().PurchasesDiscount);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(g.W.Scope, g.W.Actor, runId.Value, slip.PayslipId,
            salary, 10m, CancellationToken.None, [new GoodsItemDiscountInput(asado, 20m)]));
        slip = (await payroll.GetRunAsync(g.W.Scope, runId.Value, CancellationToken.None))!.Payslips.Single();
        Assert.Equal((3_000m, 5_700m, 24_300m, 475_700m), (slip.PurchasesItemsDiscount, slip.PurchasesDiscount, slip.PurchasesDeducted, slip.NetPaid));
        Assert.Equal(20m, slip.GoodsItems.Single(i => i.Id == asado).DiscountPercent);

        // Leaving the item discounts out keeps them.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(g.W.Scope, g.W.Actor, runId.Value, slip.PayslipId,
            salary, 10m, CancellationToken.None));
        Assert.Equal(5_700m, (await payroll.GetRunAsync(g.W.Scope, runId.Value, CancellationToken.None))!.Payslips.Single().PurchasesDiscount);

        // Paid: the goods account is settled (24.300 deducted, 5.700 forgiven) and the items are paid.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(g.W.Scope, g.W.Actor, runId.Value, g.W.Cash, Today, CancellationToken.None));
        Assert.Equal(0m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
        Assert.All(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None), i => Assert.Equal(OvertimeStatus.Paid, i.Status));
        using var owner = OpenOwner();
        Assert.Equal(5_700m, Scalar<decimal>(owner,
            "SELECT amount FROM current_account_movements WHERE customer_id = $1 AND kind = 'CreditNote'", g.Juan.CustomerId!.Value));
    }

    [Fact]
    public async Task TakingThePayslipOut_OrDiscardingTheDraft_LeavesItsGoodsPendingForTheNextPayroll()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var payroll = new PostgresPayrollStore(_dataSource!);
        await Goods().RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);
        async Task<string> Status() => Assert.Single(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None)).Status;

        var (_, first) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, CancellationToken.None);
        var slip = Assert.Single((await payroll.GetRunAsync(g.W.Scope, first!.Value, CancellationToken.None))!.Payslips);
        Assert.Equal(OvertimeStatus.InPayroll, await Status());
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.RemovePayslipAsync(g.W.Scope, first.Value, slip.PayslipId, CancellationToken.None));
        Assert.Equal(OvertimeStatus.Pending, await Status());

        var (_, second) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, CancellationToken.None);
        Assert.Single(Assert.Single((await payroll.GetRunAsync(g.W.Scope, second!.Value, CancellationToken.None))!.Payslips).GoodsItems);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.DeleteRunAsync(g.W.Scope, g.W.Actor, second.Value, CancellationToken.None));
        Assert.Equal(OvertimeStatus.Pending, await Status());
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
