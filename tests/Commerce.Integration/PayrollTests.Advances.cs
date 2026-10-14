using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Payroll;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// owner feedback 2026-10-14, A2: the advances handed out to an employee are listed by date, and voided or corrected
/// whole. An advance has two sides keyed by its id, the cash it left (a treasury movement) and the debit on the employee's
/// account: they always move together, whichever way the void starts.
/// </summary>
public sealed partial class PayrollTests
{
    private async Task<(World W, EmployeeRecord Juan)> AdvanceSeedAsync()
    {
        var w = await SeedAsync();
        var (_, juan) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: false, CancellationToken.None);
        return (w, juan!);
    }

    private async Task<Guid> GiveAsync(World w, Guid employeeId, decimal amount, DateOnly date, string? concept = null, Guid? account = null) =>
        (await Employees().GiveAdvanceAsync(w.Scope, w.Actor, employeeId, amount, date, account ?? w.Cash, concept, CancellationToken.None)).AdvanceId!.Value;

    private async Task<decimal> CashBalanceAsync(World w, Guid? account = null) =>
        (await new PostgresTreasuryStore(_dataSource!).ListAccountsAsync(w.Scope, w.Branch, Today, CancellationToken.None))
            .Single(a => a.AccountId == (account ?? w.Cash)).Balance;

    private async Task<decimal> BalanceAsync(World w, Guid employeeId) =>
        (await Employees().GetAsync(w.Scope, employeeId, CancellationToken.None))!.Balance;

    [Fact]
    public async Task AnEmployeesAdvances_AreListedInTheirRange_NewestFirst_WithTheirAccountAndConcept()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var (_, ana) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), takesGoods: false, CancellationToken.None);
        var early = await GiveAsync(w, juan.Id, 10_000m, new DateOnly(2026, 9, 20), "para el alquiler");
        var late = await GiveAsync(w, juan.Id, 5_000m, new DateOnly(2026, 10, 20));
        await GiveAsync(w, ana!.Id, 7_000m, new DateOnly(2026, 10, 20));

        var all = (await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 9, 1), Today, CancellationToken.None))!;
        Assert.Equal([late, early], all.Select(a => a.AdvanceId));
        var first = all[1];
        Assert.Equal((new DateOnly(2026, 9, 20), 10_000m, w.Cash, "Caja sueldos", "para el alquiler", false),
            (first.Date, first.Amount, first.AccountId, first.AccountName, first.Concept, first.IsVoided));
        Assert.Null(all[0].Concept);

        var october = (await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 10, 1), Today, CancellationToken.None))!;
        Assert.Equal([late], october.Select(a => a.AdvanceId));
        Assert.Null(await Employees().ListAdvancesAsync(w.Scope, Guid.NewGuid(), new DateOnly(2026, 10, 1), Today, CancellationToken.None));
    }

    [Fact]
    public async Task VoidingAnAdvance_VoidsItsCash_AndReversesItsDebit_Once()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var advance = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));
        Assert.Equal((-15_000m, -15_000m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));

        // Someone else's advance, or none: not found.
        var (_, ana) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), takesGoods: false, CancellationToken.None);
        Assert.Equal(AdvanceWriteOutcome.NotFound, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, ana!.Id, advance, "x", Today, CancellationToken.None));
        Assert.Equal(AdvanceWriteOutcome.NotFound, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, Guid.NewGuid(), "x", Today, CancellationToken.None));

        Assert.Equal(AdvanceWriteOutcome.Done, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, advance, "Mal cargado", Today, CancellationToken.None));
        Assert.Equal((0m, 0m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
        var listed = Assert.Single((await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 10, 1), Today, CancellationToken.None))!);
        Assert.Equal((true, "Mal cargado"), (listed.IsVoided, listed.VoidReason));
        Assert.NotNull(listed.VoidedAtUtc);

        // The cash movement stays, voided with the reason; the account keeps the debit and its reversal.
        using var owner = OpenOwner();
        Assert.Equal("Mal cargado", Scalar<string>(owner,
            "SELECT v.reason FROM treasury_movement_voids v JOIN treasury_movements m ON m.id = v.movement_id WHERE m.source_id = $1", advance));
        Assert.Equal("Anulación de adelanto: Mal cargado", Scalar<string>(owner,
            "SELECT concept FROM current_account_movements WHERE employee_id = $1 AND kind = 'Reversal'", juan.Id));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.advance_voided' AND entity_id = $1", juan.Id));

        // A retry (the answer lost on the way back) finds it voided and moves nothing again.
        Assert.Equal(AdvanceWriteOutcome.Done, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, advance, "Otra vez", Today, CancellationToken.None));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM current_account_movements WHERE employee_id = $1 AND kind = 'Reversal'", juan.Id));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'employee.advance_voided' AND entity_id = $1", juan.Id));
    }

    [Fact]
    public async Task AnAdvanceReversedOnlyOnTheAccount_HasItsCashVoidedByTheVoid()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var advance = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));

        // Before this change the account reversal left the cash out (Ariel's $ 15.000 of 2026-10-13).
        var party = AccountParty.Employee(juan.Id);
        var debit = (await new PostgresCurrentAccountStore(_dataSource!).ListMovementsAsync(w.Scope, party, CancellationToken.None))!.Single();
        await using (var connection = await _dataSource!.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await TenantScopeSql.ApplyAsync(connection, tx, w.Org, null, CancellationToken.None);
            Assert.Equal(ReverseMovementOutcome.Reversed, (await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, w.Org, party, debit.Id, "Mal cargado", null, Today, w.Actor, CancellationToken.None)).Outcome);
            await tx.CommitAsync();
        }

        Assert.Equal((-15_000m, 0m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
        Assert.True(Assert.Single((await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 10, 1), Today, CancellationToken.None))!).IsVoided);

        Assert.Equal(AdvanceWriteOutcome.Done, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, advance, "Mal cargado", Today, CancellationToken.None));
        Assert.Equal((0m, 0m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM current_account_movements WHERE employee_id = $1 AND kind = 'Reversal'", juan.Id));
    }

    private const string AdvanceRepairMigration = "0059_advance_cash_voids.sql";

    [Fact]
    public async Task TheRepairMigration_VoidsTheCashOfAdvancesReversedOnTheAccountOnly_Once_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var halfVoided = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));
        var intact = await GiveAsync(w, juan.Id, 12_000m, new DateOnly(2026, 10, 14));
        var party = AccountParty.Employee(juan.Id);
        var debit = (await new PostgresCurrentAccountStore(_dataSource!).ListMovementsAsync(w.Scope, party, CancellationToken.None))!
            .Single(m => m.Amount == 15_000m);
        await using (var connection = await _dataSource!.OpenConnectionAsync())
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await TenantScopeSql.ApplyAsync(connection, tx, w.Org, null, CancellationToken.None);
            await PostgresCurrentAccountStore.ReverseWithinAsync(connection, tx, w.Org, party, debit.Id, "Mal cargado", null, Today, w.Actor, CancellationToken.None);
            await tx.CommitAsync();
        }

        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, AdvanceRepairMigration);
        PostgresTestFixture.ApplyMigration(owner, AdvanceRepairMigration);

        Assert.Equal((-12_000m, -12_000m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
        const string voidOf = "SELECT v.reason FROM treasury_movement_voids v JOIN treasury_movements m ON m.id = v.movement_id WHERE m.source_id = $1";
        Assert.Equal("Anulado en la cuenta del empleado: Mal cargado", Scalar<string>(owner, voidOf, halfVoided));
        Assert.Equal(0L, Scalar<long>(owner,
            "SELECT count(*) FROM treasury_movement_voids v JOIN treasury_movements m ON m.id = v.movement_id WHERE m.source_id = $1", intact));
        Assert.Equal(w.Actor, Scalar<Guid>(owner,
            "SELECT v.voided_by_user_id FROM treasury_movement_voids v JOIN treasury_movements m ON m.id = v.movement_id WHERE m.source_id = $1", halfVoided));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", AdvanceRepairMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public async Task ReversingAnAdvanceOnTheEmployeesAccount_VoidsItsCashToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var advance = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var party = AccountParty.Employee(juan.Id);
        var debit = (await accounts.ListMovementsAsync(w.Scope, party, CancellationToken.None))!.Single();

        var result = await accounts.ReverseAsync(w.Scope, party, debit.Id, "Mal cargado", null, Today, "org-user", w.Actor, CancellationToken.None);

        Assert.Equal(ReverseMovementOutcome.Reversed, result.Outcome);
        Assert.Equal((0m, 0m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
        using var owner = OpenOwner();
        Assert.Equal("Mal cargado", Scalar<string>(owner,
            "SELECT v.reason FROM treasury_movement_voids v JOIN treasury_movements m ON m.id = v.movement_id WHERE m.source_id = $1", advance));
    }

    // owner feedback 2026-10-14 follow-up F1 (review R3-undo-race-throws / R4-advance-void-account-reverse-race): a void
    // holds the employee while it reverses the debit; a reversal from the employee's account waits for it instead of
    // racing it (the loser used to fail with a 500).
    [Fact]
    public async Task ReversingAnAdvanceOnTheEmployeesAccount_WaitsForAVoidHoldingTheEmployee()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var party = AccountParty.Employee(juan.Id);
        var debit = (await accounts.ListMovementsAsync(w.Scope, party, CancellationToken.None))!.Single();

        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await TenantScopeSql.ApplyAsync(connection, tx, w.Org, null, CancellationToken.None);
        await using (var hold = new Npgsql.NpgsqlCommand("SELECT 1 FROM employees WHERE id = $1 FOR UPDATE", connection, tx))
        {
            hold.Parameters.AddWithValue(juan.Id);
            await hold.ExecuteNonQueryAsync();
        }

        var reversing = accounts.ReverseAsync(w.Scope, party, debit.Id, "Mal cargado", null, Today, "org-user", w.Actor, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(reversing.IsCompleted);

        await tx.CommitAsync();
        Assert.Equal(ReverseMovementOutcome.Reversed, (await reversing).Outcome);
        Assert.Equal((0m, 0m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
    }

    [Fact]
    public async Task CorrectingAnAdvance_VoidsIt_AndHandsOutTheCorrectedOne_LinkedToIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var treasury = new PostgresTreasuryStore(_dataSource!);
        var (_, bank) = await treasury.CreateAccountAsync(w.Scope, w.Actor, PostgresTreasuryStore.Bank, "Banco", null, w.Branch, null, Today, CancellationToken.None);
        var advance = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));

        var (outcome, corrected) = await Employees().CorrectAdvanceAsync(
            w.Scope, w.Actor, juan.Id, advance, 12_000m, new DateOnly(2026, 10, 14), bank!.AccountId, "a cuenta", "Era de 12.000", Today, CancellationToken.None);

        Assert.Equal(AdvanceWriteOutcome.Done, outcome);
        Assert.Equal((0m, -12_000m, -12_000m), (await CashBalanceAsync(w), await CashBalanceAsync(w, bank.AccountId), await BalanceAsync(w, juan.Id)));
        var listed = (await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 10, 1), Today, CancellationToken.None))!;
        var (now, before) = (listed.Single(a => a.AdvanceId == corrected), listed.Single(a => a.AdvanceId == advance));
        Assert.Equal((12_000m, new DateOnly(2026, 10, 14), "Banco", "a cuenta", false, advance), (now.Amount, now.Date, now.AccountName, now.Concept, now.IsVoided, now.CorrectsAdvanceId));
        Assert.Equal((true, "Corregido: Era de 12.000", corrected), (before.IsVoided, before.VoidReason, before.CorrectedByAdvanceId));

        // A voided advance is not corrected again; an inactive account is refused and nothing is written.
        Assert.Equal(AdvanceWriteOutcome.Voided, (await Employees().CorrectAdvanceAsync(
            w.Scope, w.Actor, juan.Id, advance, 1m, Today, w.Cash, null, "x", Today, CancellationToken.None)).Outcome);
        Assert.Equal(TreasuryWriteOutcome.Done, await treasury.SetAccountActiveAsync(w.Scope, w.Actor, w.Cash, false, Today, CancellationToken.None));
        Assert.Equal(AdvanceWriteOutcome.AccountInactive, (await Employees().CorrectAdvanceAsync(
            w.Scope, w.Actor, juan.Id, corrected!.Value, 1m, Today, w.Cash, null, "x", Today, CancellationToken.None)).Outcome);
        Assert.Equal(-12_000m, await BalanceAsync(w, juan.Id));
        Assert.Equal(2, (await Employees().ListAdvancesAsync(w.Scope, juan.Id, new DateOnly(2026, 10, 1), Today, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task AnAdvance_IsNotVoidedWhileItsEmployeeIsInADraftPayroll_ButIsOnceThePayrollWasPaid()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (w, juan) = await AdvanceSeedAsync();
        var advance = await GiveAsync(w, juan.Id, 15_000m, new DateOnly(2026, 10, 13));
        var payroll = new PostgresPayrollStore(_dataSource!);
        var (_, runId) = await payroll.CreateRunAsync(w.Scope, w.Actor, new DateOnly(2026, 10, 1), Today, PayFrequency.Monthly, null, null, CancellationToken.None);

        // The draft read the advance in its deductions: voiding it now would leave the draft deducting it.
        Assert.Equal(AdvanceWriteOutcome.InPayrollDraft, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, advance, "x", Today, CancellationToken.None));
        Assert.Equal(AdvanceWriteOutcome.InPayrollDraft, (await Employees().CorrectAdvanceAsync(
            w.Scope, w.Actor, juan.Id, advance, 1m, Today, w.Cash, null, "x", Today, CancellationToken.None)).Outcome);
        var accounts = new PostgresCurrentAccountStore(_dataSource!);
        var party = AccountParty.Employee(juan.Id);
        var debit = (await accounts.ListMovementsAsync(w.Scope, party, CancellationToken.None))!.Single();
        Assert.Equal(ReverseMovementOutcome.InPayrollDraft,
            (await accounts.ReverseAsync(w.Scope, party, debit.Id, "x", null, Today, "org-user", w.Actor, CancellationToken.None)).Outcome);

        // Paid, the payroll deducted it: voided now, the amount is owed back to the employee and the cash gets it back.
        Assert.Equal(PayrollWriteOutcome.Done, await payroll.PayRunAsync(w.Scope, w.Actor, runId!.Value, w.Cash, Today, null, CancellationToken.None));
        var (cashBefore, balanceBefore) = (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id));
        Assert.Equal(AdvanceWriteOutcome.Done, await Employees().VoidAdvanceAsync(w.Scope, w.Actor, juan.Id, advance, "No se entregó", Today, CancellationToken.None));
        Assert.Equal((cashBefore + 15_000m, balanceBefore + 15_000m), (await CashBalanceAsync(w), await BalanceAsync(w, juan.Id)));
    }
}
