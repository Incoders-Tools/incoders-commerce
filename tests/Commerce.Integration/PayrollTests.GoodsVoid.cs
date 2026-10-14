using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Payroll;
using Commerce.Domain.Sales;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// owner feedback 2026-10-13 round 2, R1: goods registered by mistake are voided whole (or corrected: voided and
/// registered again). Voiding takes back everything the registration did: the charge on the employee's customer account,
/// the stock (in the goods' own branch) and the items still to settle (a draft payslip's goods amount lowered by them).
/// Reversing the charge on the customer account is the same void. Once a paid payroll settled them it is refused.
/// </summary>
public sealed partial class PayrollTests
{
    [Fact]
    public async Task VoidingGoods_TakesBackTheCharge_TheStock_AndTheItems_FromAnyBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var (_, registered) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12),
            [new(g.Asado, 1.5m), new(g.Chorizo, 2m)], CancellationToken.None);
        var goodsId = registered!.GoodsId;

        // Someone else's goods, or none: not found.
        var (_, ana) = await Employees().CreateAsync(g.W.Scope, g.W.Actor, Input(g.W, "Ana", 400_000m), takesGoods: true, CancellationToken.None);
        Assert.Equal(EmployeeGoodsOutcome.NotFound, await goods.VoidAsync(g.W.Scope, g.W.Actor, ana!.Id, goodsId, Today, CancellationToken.None));
        Assert.Equal(EmployeeGoodsOutcome.NotFound, await goods.VoidAsync(g.W.Scope, g.W.Actor, g.Juan.Id, Guid.NewGuid(), Today, CancellationToken.None));

        // Voided with no branch selected: the stock comes back in the goods' own branch.
        var noBranch = g.W.Scope with { BranchId = null };
        Assert.Equal(EmployeeGoodsOutcome.Done, await goods.VoidAsync(noBranch, g.W.Actor, g.Juan.Id, goodsId, Today, CancellationToken.None));

        Assert.Equal(0m, (await Employees().GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PurchasesOwed);
        Assert.Empty(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        using var owner = OpenOwner();
        Assert.Equal(0m, Scalar<decimal>(owner,
            "SELECT sum(quantity) FROM stock_movements WHERE presentation_id = $1 AND source_type = 'EmployeeGoods' AND branch_id = $2", g.Asado, g.W.Branch));
        Assert.StartsWith("Anulación: ", Scalar<string>(owner,
            "SELECT concept FROM current_account_movements WHERE customer_id = $1 AND kind = 'Reversal'", g.Juan.CustomerId!.Value));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.goods_voided' AND entity_id = $1", g.Juan.Id));

        // A retry (the answer lost on the way back) finds it voided and moves nothing again.
        Assert.Equal(EmployeeGoodsOutcome.Done, await goods.VoidAsync(g.W.Scope, g.W.Actor, g.Juan.Id, goodsId, Today, CancellationToken.None));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM current_account_movements WHERE customer_id = $1 AND kind = 'Reversal'", g.Juan.CustomerId!.Value));
        Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE source_type = 'EmployeeGoods' AND kind = 'Reversal' AND source_id = $1", goodsId));
    }

    [Fact]
    public async Task VoidingGoods_WhoseChargeWasReversedBeforeThisFix_StillReleasesThem()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var (_, registered) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);

        // Before the reversal released goods, it only reversed the charge: the items stayed pending and the stock out.
        var customer = AccountParty.Customer(g.Juan.CustomerId!.Value);
        var charge = (await new PostgresCurrentAccountStore(_dataSource!).ListMovementsAsync(g.W.Scope, customer, CancellationToken.None))!
            .Single(m => m.Kind == AccountMovementKind.Invoice);
        await using (var connection = await _dataSource!.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await TenantScopeSql.ApplyAsync(connection, tx, g.W.Org, null, CancellationToken.None);
            Assert.Equal(ReverseMovementOutcome.Reversed, (await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, g.W.Org, customer, charge.Id, "Anulación", null, Today, g.W.Actor, CancellationToken.None)).Outcome);
            await tx.CommitAsync();
        }

        Assert.Single(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        Assert.Equal(EmployeeGoodsOutcome.Done, await goods.VoidAsync(g.W.Scope, g.W.Actor, g.Juan.Id, registered!.GoodsId, Today, CancellationToken.None));
        Assert.Empty(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        using var owner = OpenOwner();
        Assert.Equal(0m, Scalar<decimal>(owner,
            "SELECT sum(quantity) FROM stock_movements WHERE source_type = 'EmployeeGoods' AND source_id = $1", registered.GoodsId));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM current_account_movements WHERE customer_id = $1 AND kind = 'Reversal'", g.Juan.CustomerId!.Value));
    }

    [Fact]
    public async Task VoidingGoodsInADraftPayroll_LowersItsGoods_AndOnceAPayrollPaidThemItIsRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, asado) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);
        var (_, chorizo) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Chorizo, 2m)], CancellationToken.None);

        var (_, runId) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, null, CancellationToken.None);
        Assert.Equal(19_000m, Assert.Single((await payroll.GetRunAsync(g.W.Scope, runId!.Value, CancellationToken.None))!.Payslips).PurchasesAmount);

        Assert.Equal(EmployeeGoodsOutcome.Done, await goods.VoidAsync(g.W.Scope, g.W.Actor, g.Juan.Id, asado!.GoodsId, Today, CancellationToken.None));
        var slip = Assert.Single((await payroll.GetRunAsync(g.W.Scope, runId.Value, CancellationToken.None))!.Payslips);
        Assert.Equal(9_000m, slip.PurchasesAmount);
        Assert.Equal("Chorizo", Assert.Single(slip.GoodsItems).ProductName);

        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(g.W.Scope, g.W.Actor, runId.Value, g.W.Cash, Today, null, CancellationToken.None));
        Assert.Equal(EmployeeGoodsOutcome.Settled, await goods.VoidAsync(g.W.Scope, g.W.Actor, g.Juan.Id, chorizo!.GoodsId, Today, CancellationToken.None));
        Assert.Equal(OvertimeStatus.Paid, Assert.Single(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None)).Status);
        using var owner = OpenOwner();
        Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE kind = 'Reversal' AND source_id = $1", chorizo.GoodsId));
    }

    [Fact]
    public async Task ReversingTheGoodsCharge_OnTheCustomerAccount_VoidsTheGoods_AndAPosSalesItems()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var goods = Goods();
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var customer = AccountParty.Customer(g.Juan.CustomerId!.Value);
        var (_, registered) = await goods.RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);

        var charge = (await accounts.ListMovementsAsync(g.W.Scope, customer, CancellationToken.None))!.Single(m => m.Kind == AccountMovementKind.Invoice);
        Assert.Equal(ReverseMovementOutcome.Reversed,
            (await accounts.ReverseAsync(g.W.Scope, customer, charge.Id, null, null, Today, "org-user", g.W.Actor, CancellationToken.None)).Outcome);
        Assert.Empty(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        using (var owner = OpenOwner())
        {
            Assert.Equal(0m, Scalar<decimal>(owner,
                "SELECT sum(quantity) FROM stock_movements WHERE source_type = 'EmployeeGoods' AND source_id = $1", registered!.GoodsId));
        }

        // A POS sale on account: its charge reversed, its items are no longer to deduct either.
        var sale = Guid.NewGuid();
        new PostgresCloudInboxStore(_dataSource!).TryApplyInbound(new CloudTenantScope(g.W.Org),
            PosSale(g.W, sale, g.Juan.CustomerId, [new(sale, 1, g.Chorizo, null, "Chorizo", "Por kg", 1m, 4_500m, 4_500m)], total: 4_500m));
        Assert.Single(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
        var posCharge = (await accounts.ListMovementsAsync(g.W.Scope, customer, CancellationToken.None))!
            .Single(m => m.Kind != AccountMovementKind.Reversal && m.ReversesMovementId is null && m.Id != charge.Id);
        Assert.Equal(ReverseMovementOutcome.Reversed,
            (await accounts.ReverseAsync(g.W.Scope, customer, posCharge.Id, null, null, Today, "org-user", g.W.Actor, CancellationToken.None)).Outcome);
        Assert.Empty(await goods.ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ReversingTheChargeOfGoodsAPaidPayrollSettled_IsRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var customer = AccountParty.Customer(g.Juan.CustomerId!.Value);
        await Goods().RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);
        var (_, runId) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, null, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(g.W.Scope, g.W.Actor, runId!.Value, g.W.Cash, Today, null, CancellationToken.None));

        var charge = (await accounts.ListMovementsAsync(g.W.Scope, customer, CancellationToken.None))!.Single(m => m.Kind == AccountMovementKind.Invoice);
        Assert.Equal(ReverseMovementOutcome.SettledByPayroll,
            (await accounts.ReverseAsync(g.W.Scope, customer, charge.Id, null, null, Today, "org-user", g.W.Actor, CancellationToken.None)).Outcome);
        Assert.Equal(OvertimeStatus.Paid, Assert.Single(await Goods().ListAsync(g.W.Scope, g.Juan.Id, CancellationToken.None)).Status);
    }
}
