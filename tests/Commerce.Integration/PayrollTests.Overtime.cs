using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Payroll;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// employee-lifecycle E2: overtime. Each record is hours x the employee's overtime hourly rate x a multiplier (a Sunday
/// proposes x2, any other day x1), its amount frozen when recorded. It stays Pending until a payroll run takes it into a
/// payslip as an "Horas extra" earning; taking the payslip, the run or that line out releases it; a paid run settles it.
/// </summary>
public sealed partial class PayrollTests
{
    private const string OvertimeMigration = "0054_employee_overtime.sql";
    private static readonly DateOnly Sunday = new(2026, 10, 11);
    private static readonly DateOnly Monday = new(2026, 10, 12);

    [Fact]
    public void Overtime_ProposesDoubleOnASunday_AndFreezesHoursTimesRateTimesMultiplier()
    {
        Assert.Equal(2m, OvertimeRules.ProposedMultiplier(Sunday));
        Assert.Equal(1m, OvertimeRules.ProposedMultiplier(Monday));
        Assert.Equal(30_000m, OvertimeRules.Amount(7.5m, 2_000m, 2m));
        Assert.Equal(3_333.33m, OvertimeRules.Amount(1m, 3_333.333m, 1m));

        Assert.True(OvertimeRules.IsValidHours(0.5m));
        Assert.True(OvertimeRules.IsValidHours(24m));
        Assert.False(OvertimeRules.IsValidHours(0m));
        Assert.False(OvertimeRules.IsValidHours(24.5m));
        Assert.False(OvertimeRules.IsValidHours(1.255m));
        Assert.True(OvertimeRules.IsValidMultiplier(1m));
        Assert.True(OvertimeRules.IsValidMultiplier(1.5m));
        Assert.True(OvertimeRules.IsValidMultiplier(3m));
        Assert.False(OvertimeRules.IsValidMultiplier(0.5m));
        Assert.False(OvertimeRules.IsValidMultiplier(3.5m));
        Assert.True(OvertimeRules.IsValidHourlyRate(2_500.5m));
        Assert.False(OvertimeRules.IsValidHourlyRate(0m));
        Assert.False(OvertimeRules.IsValidHourlyRate(1.001m));

        Assert.Equal("Horas extra (8 h)", OvertimeRules.Concept(8m));
        Assert.Equal("Horas extra (7,5 h)", OvertimeRules.Concept(7.5m));
    }

    [Fact]
    public void OvertimeMigration_RerunsSafely_AdmitsTheOvertimeLine_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, OvertimeMigration);
        PostgresTestFixture.ApplyMigration(owner, OvertimeMigration);
        Assert.Contains("Overtime", Scalar<string>(owner,
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'payslip_lines_source_check'"));
        Assert.Equal("YES", Scalar<string>(owner,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'employees' AND column_name = 'overtime_hourly_rate'"));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", OvertimeMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public async Task TheOvertimeRate_IsKeptWhenAnUpdateOmitsIt_AndZeroClearsIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = Employees();
        var (_, juan) = await store.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m) with { OvertimeHourlyRate = 2_500m },
            takesGoods: false, CancellationToken.None);
        Assert.Equal(2_500m, juan!.OvertimeHourlyRate);

        // An older terminal sends no rate: the stored one stays.
        var (_, kept) = await store.UpdateAsync(w.Scope, w.Actor, juan.Id, Input(w, "Juan", 510_000m), false, CancellationToken.None);
        Assert.Equal(2_500m, kept!.OvertimeHourlyRate);
        var (_, cleared) = await store.UpdateAsync(w.Scope, w.Actor, juan.Id, Input(w, "Juan", 510_000m) with { OvertimeHourlyRate = 0m },
            false, CancellationToken.None);
        Assert.Null(cleared!.OvertimeHourlyRate);

        // The audit says the rate that stays, not the raw input (null = kept, 0 = cleared).
        using var owner = OpenOwner();
        Assert.Equal(["2500.00", "null"], new[] { 0, 1 }.Select(n => Scalar<string>(owner,
            "SELECT (new_value -> 'OvertimeHourlyRate')::text FROM audit_log WHERE action = 'employee.updated' AND entity_id = $1 " +
            "ORDER BY id OFFSET $2 LIMIT 1", juan.Id, n)));
    }

    [Fact]
    public async Task ThePayslipsOvertimeLine_AlwaysPaysWhatItTook_AndOneWithNothingTakenIsAManualLine()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var employees = Employees();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, ana) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), false, CancellationToken.None);
        var (_, juan) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m) with { OvertimeHourlyRate = 2_000m },
            false, CancellationToken.None);
        await employees.RecordOvertimeAsync(w.Scope, w.Actor, juan!.Id, new NewOvertime(Sunday, 4m, null, null), CancellationToken.None);
        var (_, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, CancellationToken.None);
        var run = (await payroll.GetRunAsync(w.Scope, runId!.Value, CancellationToken.None))!;
        var juanSlip = run.Payslips.Single(p => p.EmployeeId == juan.Id);
        var anaSlip = run.Payslips.Single(p => p.EmployeeId == ana!.Id);

        // An edited overtime line still pays the hours it took (16.000), so the records and the payslip never disagree.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(w.Scope, w.Actor, runId.Value, juanSlip.PayslipId,
        [
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 500_000m),
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.Overtime, "Otra cosa", 1_000m),
        ], 0m, null, CancellationToken.None));
        // An overtime line on a payslip that took none is a line added by hand.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(w.Scope, w.Actor, runId.Value, anaSlip.PayslipId,
        [
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 300_000m),
            new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.Overtime, "Horas extra", 5_000m),
        ], 0m, null, CancellationToken.None));

        run = (await payroll.GetRunAsync(w.Scope, runId.Value, CancellationToken.None))!;
        var overtime = Assert.Single(run.Payslips.Single(p => p.EmployeeId == juan.Id).Lines, l => l.Source == nameof(PayslipLineSource.Overtime));
        Assert.Equal(("Horas extra (4 h)", 16_000m), (overtime.Concept, overtime.Amount));
        var byHand = run.Payslips.Single(p => p.EmployeeId == ana!.Id).Lines.Single(l => l.Concept == "Horas extra");
        Assert.Equal((nameof(PayslipLineSource.Manual), 5_000m), (byHand.Source, byHand.Amount));
    }

    [Fact]
    public async Task Overtime_IsRecordedPending_TakenByThePayroll_ReleasedWhenTakenOut_AndSettledWhenPaid()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var employees = Employees();
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, ana) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), false, CancellationToken.None);
        var (_, juan) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m) with { OvertimeHourlyRate = 2_000m },
            false, CancellationToken.None);

        // Without a rate there is nothing to multiply.
        Assert.Equal(OvertimeWriteOutcome.RateMissing,
            (await employees.RecordOvertimeAsync(w.Scope, w.Actor, ana!.Id, new NewOvertime(Monday, 2m, null, null), CancellationToken.None)).Outcome);

        // A Sunday proposes x2, a Monday x1; the amount is frozen.
        var (sundayOutcome, sunday) = await employees.RecordOvertimeAsync(w.Scope, w.Actor, juan!.Id,
            new NewOvertime(Sunday, 4m, null, "Inventario"), CancellationToken.None);
        Assert.Equal(OvertimeWriteOutcome.Done, sundayOutcome);
        Assert.Equal((2m, 2_000m, 16_000m, OvertimeStatus.Pending), (sunday!.Multiplier, sunday.HourlyRate, sunday.Amount, sunday.Status));
        var (_, monday) = await employees.RecordOvertimeAsync(w.Scope, w.Actor, juan.Id, new NewOvertime(Monday, 3.5m, null, null), CancellationToken.None);
        Assert.Equal(7_000m, monday!.Amount);
        var (_, later) = await employees.RecordOvertimeAsync(w.Scope, w.Actor, juan.Id,
            new NewOvertime(new DateOnly(2026, 11, 2), 1m, 1.5m, null), CancellationToken.None);
        Assert.Equal(3_000m, later!.Amount);

        // A later rate change does not move what was recorded.
        await employees.UpdateAsync(w.Scope, w.Actor, juan.Id, Input(w, "Juan", 500_000m) with { OvertimeHourlyRate = 9_999m }, false,
            CancellationToken.None);
        Assert.Equal(16_000m, (await employees.ListOvertimeAsync(w.Scope, juan.Id, CancellationToken.None)).Single(o => o.Id == sunday.Id).Amount);

        // The October run takes the two October records (not November's) as one earning line.
        var slip = await PrepareAsync();
        var line = Assert.Single(slip.Lines, l => l.Source == nameof(PayslipLineSource.Overtime));
        Assert.Equal((nameof(PayslipLineKind.Earning), "Horas extra (7,5 h)", 23_000m), (line.Kind, line.Concept, line.Amount));
        var listed = await employees.ListOvertimeAsync(w.Scope, juan.Id, CancellationToken.None);
        Assert.Equal([OvertimeStatus.Pending, OvertimeStatus.InPayroll, OvertimeStatus.InPayroll], listed.Select(o => o.Status));
        Assert.All(listed.Where(o => o.Status == OvertimeStatus.InPayroll), o => Assert.Equal(1, o.RunNumber));

        // In a payroll it cannot be deleted.
        Assert.Equal(OvertimeWriteOutcome.Settled, await employees.DeleteOvertimeAsync(w.Scope, w.Actor, juan.Id, sunday.Id, CancellationToken.None));

        // Taking the employee out of the draft releases it.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.RemovePayslipAsync(w.Scope, slip.RunId, slip.PayslipId, CancellationToken.None));
        await AssertAllPendingAsync();
        await payroll.DeleteRunAsync(w.Scope, w.Actor, slip.RunId, CancellationToken.None);

        // Removing the overtime line from a draft payslip releases it too.
        slip = await PrepareAsync();
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.UpdatePayslipAsync(w.Scope, w.Actor, slip.RunId, slip.PayslipId,
            [new PayslipLineInput(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", 500_000m)], 0m, null, CancellationToken.None));
        await AssertAllPendingAsync();
        await payroll.DeleteRunAsync(w.Scope, w.Actor, slip.RunId, CancellationToken.None);

        // Discarding a draft releases it.
        slip = await PrepareAsync();
        await payroll.DeleteRunAsync(w.Scope, w.Actor, slip.RunId, CancellationToken.None);
        await AssertAllPendingAsync();

        // Paying a run settles it, with the overtime in the net; pending, it can still be deleted.
        slip = await PrepareAsync();
        Assert.Equal(523_000m, slip.Earnings);
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, slip.RunId, w.Cash, Today, CancellationToken.None));
        var settled = await employees.ListOvertimeAsync(w.Scope, juan.Id, CancellationToken.None);
        Assert.Equal([OvertimeStatus.Pending, OvertimeStatus.Paid, OvertimeStatus.Paid], settled.Select(o => o.Status));
        Assert.Equal(OvertimeWriteOutcome.Settled, await employees.DeleteOvertimeAsync(w.Scope, w.Actor, juan.Id, monday.Id, CancellationToken.None));
        Assert.Equal(OvertimeWriteOutcome.Done, await employees.DeleteOvertimeAsync(w.Scope, w.Actor, juan.Id, later.Id, CancellationToken.None));
        Assert.Equal(2, (await employees.ListOvertimeAsync(w.Scope, juan.Id, CancellationToken.None)).Count);

        using var owner = OpenOwner();
        Assert.Equal(3L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.overtime_recorded' AND entity_id = $1", juan.Id));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.overtime_deleted' AND entity_id = $1", juan.Id));

        async Task AssertAllPendingAsync() =>
            Assert.All(await employees.ListOvertimeAsync(w.Scope, juan.Id, CancellationToken.None), o => Assert.Equal(OvertimeStatus.Pending, o.Status));

        async Task<(Guid RunId, Guid PayslipId, IReadOnlyList<PayslipLineRecord> Lines, decimal Earnings)> PrepareAsync()
        {
            var (created, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null,
                CancellationToken.None);
            Assert.Equal(PayrollWriteOutcome.Done, created);
            var run = (await payroll.GetRunAsync(w.Scope, runId!.Value, CancellationToken.None))!;
            var payslip = run.Payslips.Single(p => p.EmployeeId == juan.Id);
            return (runId.Value, payslip.PayslipId, payslip.Lines, payslip.Earnings);
        }
    }
}
