using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Payroll;

namespace Commerce.Integration;

/// <summary>
/// owner feedback round 2, R3: what each employee has to collect to date, for the staff list: the agreed salary plus the
/// overtime to pay, less what the employee owes (advances, goods). It is monthly: a paid payroll brings it back to the
/// salary; whatever no payroll settled carries over.
/// </summary>
public sealed partial class PayrollTests
{
    [Fact]
    public void PayToDate_IsTheSalaryPlusOvertime_LessWhatTheEmployeeOwes()
    {
        // Balance: negative when the employee owes (advances), positive when the business owes it (a salary left unpaid).
        Assert.Equal(487_000m, EmployeePayRules.PayToDate(500_000m, -15_000m, 26_000m, 24_000m, isActive: true));
        Assert.Equal(520_000m, EmployeePayRules.PayToDate(500_000m, 20_000m, 0m, 0m, isActive: true));
        Assert.Equal(-50_000m, EmployeePayRules.PayToDate(100_000m, -150_000m, 0m, 0m, isActive: true));
        // Gone, no salary accrues: only what is still to settle.
        Assert.Equal(-15_000m, EmployeePayRules.PayToDate(500_000m, -15_000m, 0m, 0m, isActive: false));
    }

    [Fact]
    public async Task TheStaffList_ReadsThePayToDate_AndAPaidPayrollBringsItBackToTheSalary()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();
        var employees = Employees();
        await Goods().RegisterAsync(g.W.Scope, g.W.Actor, g.Juan.Id, new DateOnly(2026, 10, 12), [new(g.Asado, 1m)], CancellationToken.None);
        await employees.GiveAdvanceAsync(g.W.Scope, g.W.Actor, g.Juan.Id, 100_000m, Today.AddDays(-10), g.W.Cash, null, CancellationToken.None);

        Assert.Equal(390_000m, (await employees.GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PayToDate);
        Assert.Equal(390_000m, (await employees.ListAsync(g.W.Scope, null, false, CancellationToken.None)).Single(e => e.Id == g.Juan.Id).PayToDate);

        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, runId) = await payroll.CreateRunAsync(g.W.Scope, g.W.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, null, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(g.W.Scope, g.W.Actor, runId!.Value, g.W.Cash, Today, null, CancellationToken.None));
        Assert.Equal(500_000m, (await employees.GetAsync(g.W.Scope, g.Juan.Id, CancellationToken.None))!.PayToDate);
    }
}
