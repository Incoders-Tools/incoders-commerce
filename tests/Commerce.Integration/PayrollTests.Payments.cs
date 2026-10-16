using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Payroll;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// owner feedback P5: a payslip's net is paid from one account, or split among several ("pago mixto"): each part leaves
/// its own account; the parts never exceed the net, and what they leave unpaid stays owed to the employee.
/// </summary>
public sealed partial class PayrollTests
{
    private const string PaymentsMigration = "0058_payslip_payments.sql";

    private async Task<(World W, Guid Bank, PostgresPayrollStore Payroll, Guid RunId, Guid PayslipId, Guid EmployeeId)> SplitSeedAsync()
    {
        var w = await SeedAsync();
        var (_, bank) = await new PostgresTreasuryStore(_dataSource!).CreateAccountAsync(
            w.Scope, w.Actor, PostgresTreasuryStore.Bank, "Banco Nación", null, w.Branch, null, Today, CancellationToken.None);
        var (_, juan) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), false, CancellationToken.None);
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, null,
            CancellationToken.None);
        var slip = Assert.Single((await payroll.GetRunAsync(w.Scope, runId!.Value, CancellationToken.None))!.Payslips);
        return (w, bank!.AccountId, payroll, runId.Value, slip.PayslipId, juan!.Id);
    }

    private async Task<decimal> TreasuryBalanceAsync(World w, Guid account) =>
        (await new PostgresTreasuryStore(_dataSource!).ListAccountsAsync(w.Scope, w.Branch, Today, CancellationToken.None))
        .Single(a => a.AccountId == account).Balance;

    [Fact]
    public void PaymentsMigration_RerunsSafely_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, PaymentsMigration);
        PostgresTestFixture.ApplyMigration(owner, PaymentsMigration);
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'payslip_payments'"));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", PaymentsMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public async Task APayslip_IsPaidFromSeveralAccounts_EachPartLeavingItsOwn()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, bank, payroll, runId, payslipId, employeeId) = await SplitSeedAsync();

        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today,
            [new PayslipPaymentSplit(payslipId, [new PayslipPaymentPart(w.Cash, 200_000m), new PayslipPaymentPart(bank, 300_000m)])],
            CancellationToken.None));

        Assert.Equal((-200_000m, -300_000m), (await TreasuryBalanceAsync(w, w.Cash), await TreasuryBalanceAsync(w, bank)));
        Assert.Equal(0m, (await Employees().GetAsync(w.Scope, employeeId, CancellationToken.None))!.Balance);
        var paid = Assert.Single((await payroll.GetRunAsync(w.Scope, runId, CancellationToken.None))!.Payslips);
        Assert.Equal([("Caja sueldos", 200_000m), ("Banco Nación", 300_000m)], paid.Payments.Select(p => (p.AccountName, p.Amount)));
        Assert.Equal(500_000m, paid.Paid);
    }

    [Fact]
    public async Task WhatThePartsLeaveUnpaid_StaysOwedToTheEmployee()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, bank, payroll, runId, payslipId, employeeId) = await SplitSeedAsync();

        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today,
            [new PayslipPaymentSplit(payslipId, [new PayslipPaymentPart(bank, 450_000m)])], CancellationToken.None));

        Assert.Equal((0m, -450_000m), (await TreasuryBalanceAsync(w, w.Cash), await TreasuryBalanceAsync(w, bank)));
        Assert.Equal(50_000m, (await Employees().GetAsync(w.Scope, employeeId, CancellationToken.None))!.Balance); // "Le debemos"
        Assert.Equal(450_000m, Assert.Single((await payroll.GetRunAsync(w.Scope, runId, CancellationToken.None))!.Payslips).Paid);
    }

    [Fact]
    public async Task APayment_IsRefusedWhole_WhenItsPartsExceedTheNet_NameAnotherPayslip_OrAnInactiveAccount()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, bank, payroll, runId, payslipId, _) = await SplitSeedAsync();

        Assert.Equal(PayrollWriteOutcome.PaymentExceedsNet, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today,
            [new PayslipPaymentSplit(payslipId, [new PayslipPaymentPart(w.Cash, 300_000m), new PayslipPaymentPart(bank, 200_000.01m)])],
            CancellationToken.None));
        Assert.Equal(PayrollWriteOutcome.PaymentNotInRun, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today,
            [new PayslipPaymentSplit(Guid.NewGuid(), [new PayslipPaymentPart(w.Cash, 1m)])], CancellationToken.None));
        Assert.Equal(TreasuryWriteOutcome.Done,
            await new PostgresTreasuryStore(_dataSource!).SetAccountActiveAsync(w.Scope, w.Actor, bank, false, Today, CancellationToken.None));
        Assert.Equal(PayrollWriteOutcome.AccountInactive, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today,
            [new PayslipPaymentSplit(payslipId, [new PayslipPaymentPart(bank, 100_000m)])], CancellationToken.None));

        Assert.Equal("Draft", (await payroll.GetRunAsync(w.Scope, runId, CancellationToken.None))!.Run.Status);
        Assert.Equal(0m, await TreasuryBalanceAsync(w, w.Cash));
    }

    [Fact]
    public async Task APayslipPaidWithoutParts_ReadsTheRunsAccount_ForTheRunsPaidBeforeTheParts()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, _, payroll, runId, payslipId, _) = await SplitSeedAsync();
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, runId, w.Cash, Today, null, CancellationToken.None));
        Assert.Equal([("Caja sueldos", 500_000m)],
            Assert.Single((await payroll.GetRunAsync(w.Scope, runId, CancellationToken.None))!.Payslips).Payments.Select(p => (p.AccountName, p.Amount)));

        // A run paid before the parts were kept reads its one account and the net.
        using (var owner = OpenOwner())
        {
            Exec(owner, "DELETE FROM payslip_payments WHERE payslip_id = $1", payslipId);
        }

        Assert.Equal([("Caja sueldos", 500_000m)],
            Assert.Single((await payroll.GetRunAsync(w.Scope, runId, CancellationToken.None))!.Payslips).Payments.Select(p => (p.AccountName, p.Amount)));
    }
}
