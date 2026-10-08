using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Identity;
using Commerce.Domain.Payroll;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// The staff and their internal payroll (0050, PRD 9.19): the arithmetic of a payslip (earnings minus deductions minus the
/// goods, with the discount the owner grants), the staff file (file numbers, the customer linked for the goods), an advance
/// (treasury out, a debit on the employee's account) and a branch payroll prepared, edited and paid: the employee's
/// account settles, the goods are settled on its customer account, the net leaves the treasury, once, all audited.
/// </summary>
[Collection("Postgres")]
public sealed class PayrollTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 31);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PayrollTests()
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

    public void Dispose() => _dataSource?.Dispose();

    [Fact]
    public void APayslip_IsEarnings_MinusDeductions_MinusTheGoodsLessTheirDiscount_AndNeverPaysOutANegativeNet()
    {
        var payslip = new Payslip(
        [
            new PayslipLine(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 500_000m),
            new PayslipLine(PayslipLineKind.Earning, PayslipLineSource.Manual, "Horas extra", 20_000m),
            new PayslipLine(PayslipLineKind.Deduction, PayslipLineSource.Advances, "Adelantos", 100_000m),
            new PayslipLine(PayslipLineKind.Deduction, PayslipLineSource.Manual, "Faltante de caja", 3_000m),
        ], new PayslipPurchases(50_000m, 10m));

        Assert.Equal((520_000m, 103_000m, 100_000m, 3_000m), (payslip.Earnings, payslip.Deductions, payslip.AdvancesDeducted, payslip.OtherDeductions));
        Assert.Equal((5_000m, 45_000m), (payslip.Purchases.Discount, payslip.Purchases.Deducted));
        Assert.Equal(372_000m, payslip.Net);

        var owes = new Payslip([new PayslipLine(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo", 10_000m)], new PayslipPurchases(30_000m, 0m));
        Assert.Equal((-20_000m, 0m), (owes.Net, owes.NetPaid));
        Assert.Equal(33.33m, new PayslipPurchases(100m, 33.33m).Discount);
        Assert.True(PayslipRules.IsValidDiscountPercent(12.5m));
        Assert.False(PayslipRules.IsValidDiscountPercent(100.01m));
    }

    [Fact]
    public void AnEmployeesAccount_ReadsLikeASuppliers_WhatTheBusinessOwesIt()
    {
        Assert.Equal(AccountDirection.Credit, PartyAccountRules.DebtDirection(AccountPartyKind.Employee));
        Assert.True(PartyAccountRules.TryResolveDirection(AccountPartyKind.Employee, AccountMovementKind.Invoice, null, out var salary, out _));
        Assert.True(PartyAccountRules.TryResolveDirection(AccountPartyKind.Employee, AccountMovementKind.Payment, null, out var paid, out _));
        Assert.Equal((AccountDirection.Credit, AccountDirection.Debit), (salary, paid));
    }

    private sealed record World(Guid Org, Guid Branch, Guid Actor, CloudTenantScope Scope, Guid Cash);

    private async Task<World> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Payroll Org"), new NewBranch(branch, "Arrecifes"),
            new NewUserAccount(actor, $"payroll-{org}@example.com", "hash", [branch], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None));
        var scope = new CloudTenantScope(org, BranchId: branch);
        var (_, cash) = await new PostgresTreasuryStore(_dataSource!).CreateAccountAsync(
            scope, actor, PostgresTreasuryStore.Other, "Caja sueldos", null, branch, null, Today, CancellationToken.None);
        return new World(org, branch, actor, scope, cash!.AccountId);
    }

    private PostgresEmployeeStore Employees() => new(_dataSource!, new PostgresCustomerStore(_dataSource!));

    private static EmployeeInput Input(World w, string first, decimal salary, int? fileNumber = null, string frequency = PayFrequency.Monthly) =>
        new(w.Branch, fileNumber, first, "Pérez", "30111222", null, null, null, null, null, new DateOnly(2025, 1, 1), frequency, salary, null);

    [Fact]
    public async Task TheStaffFile_NumbersEachEmployee_LinksACustomerForTheGoods_AndDeactivates()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = Employees();

        var (_, juan) = await store.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: true, CancellationToken.None);
        var (_, ana) = await store.CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), takesGoods: false, CancellationToken.None);
        Assert.Equal((1, 2), (juan!.FileNumber, ana!.FileNumber));
        Assert.NotNull(juan.CustomerId);
        Assert.Null(ana.CustomerId);
        Assert.Equal(EmployeeWriteOutcome.FileNumberInUse,
            (await store.CreateAsync(w.Scope, w.Actor, Input(w, "Otro", 1m, fileNumber: 1), false, CancellationToken.None)).Outcome);

        using (var owner = OpenOwner())
        {
            Assert.Equal("Pérez, Juan (personal)", Scalar<string>(owner, "SELECT display_name FROM customers WHERE id = $1", juan.CustomerId!.Value));
        }

        // Enabling the goods later links its customer too.
        var (_, updated) = await store.UpdateAsync(w.Scope, w.Actor, ana.Id, Input(w, "Ana", 320_000m), takesGoods: true, CancellationToken.None);
        Assert.Equal(320_000m, updated!.BaseSalary);
        Assert.NotNull(updated.CustomerId);

        Assert.Equal(EmployeeWriteOutcome.Done, await store.SetActiveAsync(w.Scope, w.Actor, ana.Id, false, Today, CancellationToken.None));
        Assert.Single(await store.ListAsync(w.Scope, w.Branch, includeInactive: false, CancellationToken.None));
        var all = await store.ListAsync(w.Scope, w.Branch, includeInactive: true, CancellationToken.None);
        Assert.Equal(Today, all.Single(e => e.Id == ana.Id).TerminationDate);
    }

    [Fact]
    public async Task APayroll_IsPreparedEditedAndPaid_SettlingTheEmployeesAccountAndItsGoods_AndTakingTheNetOutOfTheTreasury_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var employees = Employees();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var (_, juan) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: true, CancellationToken.None);
        await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Weekly", 90_000m, frequency: PayFrequency.Weekly), false, CancellationToken.None);

        // An advance of 100.000 from the treasury, and 50.000 of goods taken on its customer account.
        Assert.Equal(EmployeeWriteOutcome.Done,
            (await employees.GiveAdvanceAsync(w.Scope, w.Actor, juan!.Id, 100_000m, Today.AddDays(-10), w.Cash, "para el alquiler", CancellationToken.None)).Outcome);
        await accounts.RegisterAsync(w.Scope, AccountParty.Customer(juan.CustomerId!.Value),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Invoice, AccountDirection.Debit, 50_000m, Today.AddDays(-5), null, null,
                "Venta en el local", w.Actor), "org-user", w.Actor, CancellationToken.None);
        Assert.Equal(-100_000m, (await employees.GetAsync(w.Scope, juan.Id, CancellationToken.None))!.Balance);

        // The monthly run: only the monthly employee, with its salary, its advance and its goods.
        var (created, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.Done, created);
        var run = (await payroll.GetRunAsync(w.Scope, runId!.Value, CancellationToken.None))!;
        var slip = Assert.Single(run.Payslips);
        Assert.Equal((1, 500_000m, 100_000m, 50_000m), (run.Run.RunNumber, slip.Earnings, slip.Deductions, slip.PurchasesAmount));

        // Overtime, and 10 % off the goods.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(w.Scope, w.Actor, run.Run.RunId, slip.PayslipId,
        [
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 500_000m),
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.Manual, "Horas extra", 20_000m),
            new PayslipLineInput(PayslipLineKind.Deduction, PayslipLineSource.Advances, "Adelantos", 100_000m),
        ], 10m, CancellationToken.None));
        slip = (await payroll.GetRunAsync(w.Scope, run.Run.RunId, CancellationToken.None))!.Payslips.Single();
        Assert.Equal((5_000m, 45_000m, 375_000m), (slip.PurchasesDiscount, slip.PurchasesDeducted, slip.NetPaid));

        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, run.Run.RunId, w.Cash, Today, CancellationToken.None));
        Assert.Equal(PayrollWriteOutcome.NotDraft, await payroll.PayRunAsync(w.Scope, w.Actor, run.Run.RunId, w.Cash, Today, CancellationToken.None));
        Assert.Equal(PayrollWriteOutcome.NotDraft, await payroll.DeleteRunAsync(w.Scope, w.Actor, run.Run.RunId, CancellationToken.None));

        // The employee's account is settled, its goods too, and the treasury paid the advance and the net.
        var after = (await employees.GetAsync(w.Scope, juan.Id, CancellationToken.None))!;
        Assert.Equal((0m, 0m), (after.Balance, after.PurchasesOwed));
        var cash = (await new PostgresTreasuryStore(_dataSource!).ListAccountsAsync(w.Scope, w.Branch, Today, CancellationToken.None))
            .Single(a => a.AccountId == w.Cash);
        Assert.Equal(-475_000m, cash.Balance);

        var paid = (await payroll.GetRunAsync(w.Scope, run.Run.RunId, CancellationToken.None))!;
        Assert.Equal(("Paid", Today, "Caja sueldos", 375_000m), (paid.Run.Status, paid.Run.PaidOn, paid.PaymentAccountName, paid.Run.TotalNet));
        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'payroll.paid' AND entity_id = $1", run.Run.RunId));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.advance_given' AND entity_id = $1", juan.Id));
    }

    [Fact]
    public async Task ADraft_IsDiscarded_OrLosesAnEmployee_AndARunWithoutEmployeesIsRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var payroll = new PostgresPayrollStore(_dataSource!);
        Assert.Equal(PayrollWriteOutcome.NoEmployees,
            (await payroll.CreateRunAsync(w.Scope, w.Actor, Today, Today, null, null, CancellationToken.None)).Outcome);

        await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 1_000m), false, CancellationToken.None);
        await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 1_000m), false, CancellationToken.None);
        var (_, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, Today, Today, null, null, CancellationToken.None);
        var run = (await payroll.GetRunAsync(w.Scope, runId!.Value, CancellationToken.None))!;
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.RemovePayslipAsync(w.Scope, run.Run.RunId, run.Payslips[0].PayslipId, CancellationToken.None));
        Assert.Single((await payroll.GetRunAsync(w.Scope, run.Run.RunId, CancellationToken.None))!.Payslips);

        Assert.Equal(PayrollWriteOutcome.Done, await payroll.DeleteRunAsync(w.Scope, w.Actor, run.Run.RunId, CancellationToken.None));
        Assert.Null(await payroll.GetRunAsync(w.Scope, run.Run.RunId, CancellationToken.None));
        // A discarded draft leaves nothing behind: the period is prepared again from scratch.
        Assert.Equal(PayrollWriteOutcome.Done, (await payroll.CreateRunAsync(w.Scope, w.Actor, Today, Today, null, null, CancellationToken.None)).Outcome);
    }
}
