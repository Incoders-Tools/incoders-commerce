using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Payroll;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// employee-lifecycle E3: termination ("Gestionar baja": date and reason) and the final settlement, a payroll run of the
/// terminated employee alone, through the same flow. It runs from the day after the last paid period that included the
/// employee (or its hire date, or the 1st of the termination month) to the termination date, proposes the agreed salary
/// in proportion to the days worked (owner decision 2026-10-12), and takes the pending overtime, advances and goods like
/// any payslip. A regular run never takes an employee who was given the leave, so it cannot be paid twice.
/// </summary>
public sealed partial class PayrollTests
{
    private const string TerminationMigration = "0055_employee_termination_reason.sql";

    [Fact]
    public void TheFinalSettlement_StartsAfterTheLastPaidPeriod_AndProratesTheSalaryByTheDaysWorked()
    {
        var leave = new DateOnly(2026, 10, 10);
        Assert.Equal(new DateOnly(2026, 10, 1), FinalSettlementRules.PeriodFrom(new DateOnly(2026, 9, 30), new DateOnly(2025, 1, 1), leave));
        Assert.Equal(new DateOnly(2026, 10, 1), FinalSettlementRules.PeriodFrom(null, new DateOnly(2024, 3, 15), leave));
        Assert.Equal(new DateOnly(2026, 10, 5), FinalSettlementRules.PeriodFrom(null, new DateOnly(2026, 10, 5), leave));
        Assert.Equal(new DateOnly(2026, 10, 1), FinalSettlementRules.PeriodFrom(null, null, leave));

        // Monthly: the days of the termination month; biweekly: 15; weekly: 7.
        Assert.Equal(193_548.39m, FinalSettlementRules.ProportionalSalary(600_000m, PayFrequency.Monthly, 10, leave));
        Assert.Equal((31, 10), (FinalSettlementRules.PeriodDays(PayFrequency.Monthly, leave), FinalSettlementRules.DaysWorked(new DateOnly(2026, 10, 1), leave)));
        Assert.Equal(50_000m, FinalSettlementRules.ProportionalSalary(150_000m, PayFrequency.Biweekly, 5, leave));
        Assert.Equal(30_000m, FinalSettlementRules.ProportionalSalary(70_000m, PayFrequency.Weekly, 3, leave));
        // Already paid past the termination: no days left to pay.
        Assert.Equal(0, FinalSettlementRules.DaysWorked(new DateOnly(2026, 11, 1), leave));
        Assert.Equal("Sueldo básico proporcional (10 de 31 días)", FinalSettlementRules.Concept(10, 31));
    }

    [Fact]
    public void TerminationMigration_RerunsSafely_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, TerminationMigration);
        PostgresTestFixture.ApplyMigration(owner, TerminationMigration);
        Assert.Equal("YES", Scalar<string>(owner,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'employees' AND column_name = 'termination_reason'"));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", TerminationMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public async Task ALeave_KeepsItsDateAndReason_AndReinstatingClearsBoth()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = Employees();
        var (_, juan) = await store.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 600_000m), false, CancellationToken.None);

        Assert.Equal(EmployeeWriteOutcome.Done,
            await store.SetActiveAsync(w.Scope, w.Actor, juan!.Id, false, new DateOnly(2026, 10, 10), "Renuncia", CancellationToken.None));
        var left = (await store.GetAsync(w.Scope, juan.Id, CancellationToken.None))!;
        Assert.Equal((false, new DateOnly(2026, 10, 10), "Renuncia"), (left.IsActive, left.TerminationDate, left.TerminationReason));

        Assert.Equal(EmployeeWriteOutcome.Done, await store.SetActiveAsync(w.Scope, w.Actor, juan.Id, true, null, null, CancellationToken.None));
        var back = (await store.GetAsync(w.Scope, juan.Id, CancellationToken.None))!;
        Assert.Equal((true, (DateOnly?)null, (string?)null), (back.IsActive, back.TerminationDate, back.TerminationReason));
    }

    [Fact]
    public async Task TheFinalSettlement_PaysTheDaysWorkedPlusTheOvertimeLessTheAdvances_AndOnlyOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var employees = Employees();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, juan) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 600_000m) with { OvertimeHourlyRate = 2_000m },
            false, CancellationToken.None);
        var (_, ana) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), false, CancellationToken.None);

        // September is paid.
        var (_, september) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), PayFrequency.Monthly,
            null, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, september!.Value, w.Cash, new DateOnly(2026, 9, 30),
            CancellationToken.None));

        // In October Juan works overtime, takes an advance and leaves on the 10th.
        await employees.RecordOvertimeAsync(w.Scope, w.Actor, juan!.Id, new NewOvertime(new DateOnly(2026, 10, 5), 3m, 1m, null), CancellationToken.None);
        await employees.GiveAdvanceAsync(w.Scope, w.Actor, juan.Id, 100_000m, new DateOnly(2026, 10, 3), w.Cash, null, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.EmployeeActive,
            (await payroll.CreateFinalSettlementAsync(w.Scope, w.Actor, juan.Id, CancellationToken.None)).Outcome);
        await employees.SetActiveAsync(w.Scope, w.Actor, juan.Id, false, new DateOnly(2026, 10, 10), "Renuncia", CancellationToken.None);

        // The regular October run does not take him.
        var (_, october) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null,
            CancellationToken.None);
        Assert.Equal([ana!.Id], (await payroll.GetRunAsync(w.Scope, october!.Value, CancellationToken.None))!.Payslips.Select(p => p.EmployeeId));

        // His final settlement: from October 1st to the 10th, 10 of 31 days, the overtime, less the advance.
        var (outcome, finalRun) = await payroll.CreateFinalSettlementAsync(w.Scope, w.Actor, juan.Id, CancellationToken.None);
        Assert.Equal(PayrollWriteOutcome.Done, outcome);
        var run = (await payroll.GetRunAsync(w.Scope, finalRun!.Value, CancellationToken.None))!;
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), "Draft"), (run.Run.PeriodFrom, run.Run.PeriodTo, run.Run.Status));
        Assert.Equal("Liquidación final de Pérez, Juan (baja 10/10/2026: Renuncia)", run.Run.Notes);
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(
        [
            ("Earning", "BaseSalary", "Sueldo básico proporcional (10 de 31 días)", 193_548.39m),
            ("Deduction", "Advances", "Adelantos y saldo pendiente", 100_000m),
            ("Earning", "Overtime", "Horas extra (3 h)", 6_000m),
        ], slip.Lines.Select(l => (l.Kind, l.Source, l.Concept, l.Amount)));

        // Only once: while that draft exists another is refused; the employee must belong to the selected branch.
        Assert.Equal(PayrollWriteOutcome.EmployeeInDraft,
            (await payroll.CreateFinalSettlementAsync(w.Scope, w.Actor, juan.Id, CancellationToken.None)).Outcome);
        Assert.Equal(PayrollWriteOutcome.OtherBranch, (await payroll.CreateFinalSettlementAsync(
            new CloudTenantScope(w.Org, BranchId: Guid.NewGuid()), w.Actor, juan.Id, CancellationToken.None)).Outcome);
        Assert.Equal(PayrollWriteOutcome.NotFound,
            (await payroll.CreateFinalSettlementAsync(w.Scope, w.Actor, Guid.NewGuid(), CancellationToken.None)).Outcome);

        // Reinstated while that draft is open he would be paid the final settlement and the regular runs too: refused.
        Assert.Equal(EmployeeWriteOutcome.InPayrollDraft,
            await employees.SetActiveAsync(w.Scope, w.Actor, juan.Id, true, null, null, CancellationToken.None));

        // Once paid, his leave is settled: no second final settlement; reinstating him is allowed again.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, finalRun.Value, w.Cash, Today, CancellationToken.None));
        Assert.Equal(PayrollWriteOutcome.AlreadySettled,
            (await payroll.CreateFinalSettlementAsync(w.Scope, w.Actor, juan.Id, CancellationToken.None)).Outcome);
        Assert.Equal(EmployeeWriteOutcome.Done, await employees.SetActiveAsync(w.Scope, w.Actor, juan.Id, true, null, null, CancellationToken.None));
    }
}
