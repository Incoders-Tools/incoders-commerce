using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Payroll;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

public sealed record PayrollRunSummary(
    Guid RunId,
    int RunNumber,
    Guid BranchId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    string? PayFrequency,
    string Status,
    string? Notes,
    int EmployeeCount,
    decimal TotalNet,
    DateOnly? PaidOn,
    Guid? PaymentAccountId,
    DateTimeOffset CreatedAtUtc);

public sealed record PayslipLineRecord(int LineNo, string Kind, string Source, string Concept, decimal Amount);

/// <summary>
/// One employee's payslip of a run, with its computed totals (<see cref="Payslip"/>). <see cref="PreviousBalance"/>: what
/// the employee's account held when the run was read (positive: owed to the employee from before; it is not part of this
/// pay); the advances still pending are already a deduction line.
/// </summary>
public sealed record PayslipRecord(
    Guid PayslipId,
    Guid EmployeeId,
    int FileNumber,
    string EmployeeName,
    string? RoleName,
    string? Cuil,
    bool TakesGoods,
    IReadOnlyList<PayslipLineRecord> Lines,
    decimal PurchasesAmount,
    decimal PurchasesDiscountPercent,
    decimal PurchasesDiscount,
    decimal PurchasesDeducted,
    decimal Earnings,
    decimal Deductions,
    decimal Net,
    decimal NetPaid);

public sealed record PayrollRunDetail(PayrollRunSummary Run, string? BranchName, string? PaymentAccountName, IReadOnlyList<PayslipRecord> Payslips);

public sealed record PayslipLineInput(PayslipLineKind Kind, PayslipLineSource Source, string Concept, decimal Amount);

public enum PayrollWriteOutcome
{
    Done,
    NotFound,
    NotDraft,
    NoEmployees,
    AccountNotFound,
    AccountInactive,
    NothingToPay,
}

/// <summary>
/// The internal payroll of a branch (0050; PRD 9.19, no legal contributions). A run of a period is prepared as a Draft: a
/// payslip per active employee of the branch (paid that often, when a frequency is given) with its base salary, the
/// advances its account still has pending, and what its linked customer owes for goods; earnings and deductions can be
/// edited and the goods discount set. Paying it posts, per payslip and in ONE transaction for the whole run, keyed by the
/// payslip so nothing posts twice:
/// <list type="bullet">
/// <item>on the employee's account: the earnings (Invoice: the business owes them), the other deductions and the goods
/// deducted (Adjustments, Debit) and the net paid (Payment). The advances were already on it: with the earnings they net out.</item>
/// <item>on the linked customer's account: what was deducted (Payment) and the discount the owner granted (CreditNote),
/// so the goods are settled.</item>
/// <item>the net paid Out of the chosen treasury account (SalaryPayment).</item>
/// </list>
/// A Paid run is final (its movements are corrected in the treasury and the accounts); a Draft can be discarded.
/// </summary>
public sealed class PostgresPayrollStore
{
    public const string EarningsSource = "PayrollEarnings";
    public const string DeductionsSource = "PayrollDeductions";
    public const string GoodsSource = "PayrollGoods";
    public const string GoodsDiscountSource = "PayrollGoodsDiscount";
    public const string PaymentSource = "PayrollPayment";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresPayrollStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string RunSelect = """
        SELECT r.id, r.run_number, r.branch_id, r.period_from, r.period_to, r.pay_frequency, r.status, r.notes,
               (SELECT count(*) FROM payslips p WHERE p.organization_id = r.organization_id AND p.run_id = r.id)::integer,
               r.paid_on, r.payment_account_id, r.created_at_utc
        FROM payroll_runs r
        """;

    private static PayrollRunSummary ReadRun(NpgsqlDataReader r, decimal totalNet) => new(
        r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetFieldValue<DateOnly>(3), r.GetFieldValue<DateOnly>(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8),
        totalNet, r.IsDBNull(9) ? null : r.GetFieldValue<DateOnly>(9), r.IsDBNull(10) ? null : r.GetGuid(10),
        r.GetFieldValue<DateTimeOffset>(11));

    /// <summary>The runs of the selected branch, newest first, with their total net pay.</summary>
    public async Task<IReadOnlyList<PayrollRunSummary>> ListRunsAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var runs = new List<PayrollRunSummary>();
        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand($"{RunSelect} WHERE r.branch_id = $1 ORDER BY r.period_from DESC, r.run_number DESC", connection, tx))
        {
            cmd.Parameters.AddWithValue(scope.BranchId!.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                runs.Add(ReadRun(reader, 0m));
            }
        }

        var withTotals = new List<PayrollRunSummary>(runs.Count);
        foreach (var run in runs)
        {
            var payslips = await ReadPayslipsAsync(connection, tx, run.RunId, ct);
            withTotals.Add(run with { TotalNet = payslips.Sum(p => p.NetPaid) });
        }

        await tx.CommitAsync(ct);
        return withTotals;
    }

    public async Task<PayrollRunDetail?> GetRunAsync(CloudTenantScope scope, Guid runId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var detail = await ReadDetailAsync(connection, tx, scope, runId, ct);
        await tx.CommitAsync(ct);
        return detail;
    }

    private static async Task<PayrollRunDetail?> ReadDetailAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Guid runId, CancellationToken ct)
    {
        PayrollRunSummary? run = null;
        await using (var cmd = new NpgsqlCommand($"{RunSelect} WHERE r.id = $1 AND r.branch_id = $2", connection, tx))
        {
            cmd.Parameters.AddWithValue(runId);
            cmd.Parameters.AddWithValue(scope.BranchId!.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                run = ReadRun(reader, 0m);
            }
        }

        if (run is null)
        {
            return null;
        }

        var payslips = await ReadPayslipsAsync(connection, tx, runId, ct);
        string? branchName, accountName = null;
        await using (var branch = new NpgsqlCommand("SELECT name FROM branches WHERE id = $1", connection, tx))
        {
            branch.Parameters.AddWithValue(run.BranchId);
            branchName = await branch.ExecuteScalarAsync(ct) as string;
        }

        if (run.PaymentAccountId is { } accountId)
        {
            await using var account = new NpgsqlCommand("SELECT name FROM treasury_accounts WHERE id = $1", connection, tx);
            account.Parameters.AddWithValue(accountId);
            accountName = await account.ExecuteScalarAsync(ct) as string;
        }

        return new PayrollRunDetail(run with { TotalNet = payslips.Sum(p => p.NetPaid) }, branchName, accountName, payslips);
    }

    private static async Task<List<PayslipRecord>> ReadPayslipsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, CancellationToken ct)
    {
        var heads = new List<(Guid Id, Guid EmployeeId, int File, string Name, string? Role, string? Cuil, bool Goods, decimal Purchases, decimal Percent)>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id, p.employee_id, e.file_number, e.last_name || ', ' || e.first_name, r.name, e.cuil, e.customer_id IS NOT NULL,
                   p.purchases_amount, p.purchases_discount_percent
            FROM payslips p
            JOIN employees e ON e.organization_id = p.organization_id AND e.id = p.employee_id
            LEFT JOIN employee_roles r ON r.organization_id = e.organization_id AND r.id = e.role_id
            WHERE p.run_id = $1
            ORDER BY lower(e.last_name), lower(e.first_name)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(runId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                heads.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6), reader.GetDecimal(7), reader.GetDecimal(8)));
            }
        }

        var payslips = new List<PayslipRecord>(heads.Count);
        foreach (var head in heads)
        {
            var lines = new List<PayslipLineRecord>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT line_no, kind, source, concept, amount FROM payslip_lines WHERE payslip_id = $1 ORDER BY line_no", connection, tx))
            {
                cmd.Parameters.AddWithValue(head.Id);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    lines.Add(new PayslipLineRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4)));
                }
            }

            var payslip = ToPayslip(lines, head.Purchases, head.Percent);
            payslips.Add(new PayslipRecord(
                head.Id, head.EmployeeId, head.File, head.Name, head.Role, head.Cuil, head.Goods, lines, head.Purchases, head.Percent,
                payslip.Purchases.Discount, payslip.Purchases.Deducted, payslip.Earnings, payslip.Deductions, payslip.Net, payslip.NetPaid));
        }

        return payslips;
    }

    private static Payslip ToPayslip(IEnumerable<PayslipLineRecord> lines, decimal purchases, decimal percent) => new(
        [.. lines.Select(line => new PayslipLine(
            Enum.Parse<PayslipLineKind>(line.Kind), Enum.Parse<PayslipLineSource>(line.Source), line.Concept, line.Amount))],
        new PayslipPurchases(purchases, percent));

    private static string PeriodText(DateOnly from, DateOnly to) => $"{from:dd/MM/yyyy} al {to:dd/MM/yyyy}";

    /// <summary>
    /// Prepares a Draft run of the selected branch for a period: one payslip per active employee of the branch (paid with
    /// <paramref name="payFrequency"/>, when given) with its base salary, the advances pending on its account (what it owes
    /// the business) and what its linked customer owes for goods.
    /// </summary>
    public async Task<(PayrollWriteOutcome Outcome, Guid? RunId)> CreateRunAsync(
        CloudTenantScope scope, Guid actorId, DateOnly periodFrom, DateOnly periodTo, string? payFrequency, string? notes, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var branchId = scope.BranchId!.Value;

        var employees = await PostgresEmployeeStore.ReadAsync(connection, tx,
            "WHERE e.branch_id = $1 AND e.is_active AND ($2::text IS NULL OR e.pay_frequency = $2) ORDER BY lower(e.last_name)",
            ct, (NpgsqlTypes.NpgsqlDbType.Uuid, branchId), (NpgsqlTypes.NpgsqlDbType.Text, payFrequency));
        if (employees.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return (PayrollWriteOutcome.NoEmployees, null);
        }

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('payroll-run:' || $1::text))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(branchId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        int runNumber;
        await using (var next = new NpgsqlCommand("SELECT COALESCE(MAX(run_number), 0) + 1 FROM payroll_runs WHERE branch_id = $1", connection, tx))
        {
            next.Parameters.AddWithValue(branchId);
            runNumber = (int)(await next.ExecuteScalarAsync(ct))!;
        }

        var runId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO payroll_runs (organization_id, id, branch_id, run_number, period_from, period_to, pay_frequency, notes, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(scope.OrganizationId);
            insert.Parameters.AddWithValue(runId);
            insert.Parameters.AddWithValue(branchId);
            insert.Parameters.AddWithValue(runNumber);
            insert.Parameters.AddWithValue(periodFrom);
            insert.Parameters.AddWithValue(periodTo);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)payFrequency ?? DBNull.Value);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)notes ?? DBNull.Value);
            insert.Parameters.AddWithValue(actorId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        foreach (var employee in employees)
        {
            var lines = new List<PayslipLineInput>();
            if (employee.BaseSalary > 0m)
            {
                lines.Add(new(PayslipLineKind.Earning, PayslipLineSource.BaseSalary, "Sueldo básico", employee.BaseSalary));
            }

            // What the employee owes on its account (advances handed out, an earlier negative net): deducted now.
            var balance = await PostgresEmployeeStore.BalanceAsync(connection, tx, AccountParty.Employee(employee.Id), ct);
            if (balance < 0m)
            {
                lines.Add(new(PayslipLineKind.Deduction, PayslipLineSource.Advances, "Adelantos y saldo pendiente", -balance));
            }

            var purchases = employee.CustomerId is { } customer
                ? Math.Max(await PostgresEmployeeStore.BalanceAsync(connection, tx, AccountParty.Customer(customer), ct), 0m)
                : 0m;
            await InsertPayslipAsync(connection, tx, scope.OrganizationId, runId, Guid.NewGuid(), employee.Id, purchases, 0m, lines, ct);
        }

        await PostgresEmployeeStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, runId, "payroll.prepared",
            new { runNumber, branchId, periodFrom, periodTo, payFrequency, employees = employees.Count }, ct, entityType: "payroll-run");
        await tx.CommitAsync(ct);
        return (PayrollWriteOutcome.Done, runId);
    }

    private static async Task InsertPayslipAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid runId, Guid payslipId, Guid employeeId,
        decimal purchases, decimal percent, IReadOnlyList<PayslipLineInput> lines, CancellationToken ct)
    {
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO payslips (organization_id, id, run_id, employee_id, purchases_amount, purchases_discount_percent)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(organizationId);
            insert.Parameters.AddWithValue(payslipId);
            insert.Parameters.AddWithValue(runId);
            insert.Parameters.AddWithValue(employeeId);
            insert.Parameters.AddWithValue(purchases);
            insert.Parameters.AddWithValue(percent);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await InsertLinesAsync(connection, tx, organizationId, payslipId, lines, ct);
    }

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid payslipId, IReadOnlyList<PayslipLineInput> lines, CancellationToken ct)
    {
        var lineNo = 0;
        foreach (var line in lines)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO payslip_lines (organization_id, payslip_id, line_no, kind, source, concept, amount)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, connection, tx);
            insert.Parameters.AddWithValue(organizationId);
            insert.Parameters.AddWithValue(payslipId);
            insert.Parameters.AddWithValue(++lineNo);
            insert.Parameters.AddWithValue(line.Kind.ToString());
            insert.Parameters.AddWithValue(line.Source.ToString());
            insert.Parameters.AddWithValue(line.Concept.Trim());
            insert.Parameters.AddWithValue(line.Amount);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<string?> LockRunStatusAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid runId, Guid branchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT status FROM payroll_runs WHERE id = $1 AND branch_id = $2 FOR UPDATE", connection, tx);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(branchId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Replaces a Draft payslip's lines and sets its goods discount percentage.</summary>
    public async Task<PayrollWriteOutcome> UpdatePayslipAsync(
        CloudTenantScope scope, Guid actorId, Guid runId, Guid payslipId, IReadOnlyList<PayslipLineInput> lines, decimal purchasesDiscountPercent,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        if (await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct) is not { } status)
        {
            await tx.RollbackAsync(ct);
            return PayrollWriteOutcome.NotFound;
        }

        if (status != "Draft")
        {
            await tx.RollbackAsync(ct);
            return PayrollWriteOutcome.NotDraft;
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE payslips SET purchases_discount_percent = $3 WHERE id = $1 AND run_id = $2", connection, tx))
        {
            update.Parameters.AddWithValue(payslipId);
            update.Parameters.AddWithValue(runId);
            update.Parameters.AddWithValue(purchasesDiscountPercent);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return PayrollWriteOutcome.NotFound;
            }
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM payslip_lines WHERE payslip_id = $1", connection, tx))
        {
            delete.Parameters.AddWithValue(payslipId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await InsertLinesAsync(connection, tx, scope.OrganizationId, payslipId, lines, ct);
        await tx.CommitAsync(ct);
        return PayrollWriteOutcome.Done;
    }

    /// <summary>Takes an employee out of a Draft run (paid apart, or not this time).</summary>
    public async Task<PayrollWriteOutcome> RemovePayslipAsync(CloudTenantScope scope, Guid runId, Guid payslipId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var status = await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct);
        if (status != "Draft")
        {
            await tx.RollbackAsync(ct);
            return status is null ? PayrollWriteOutcome.NotFound : PayrollWriteOutcome.NotDraft;
        }

        await using var delete = new NpgsqlCommand("DELETE FROM payslips WHERE id = $1 AND run_id = $2", connection, tx);
        delete.Parameters.AddWithValue(payslipId);
        delete.Parameters.AddWithValue(runId);
        var removed = await delete.ExecuteNonQueryAsync(ct) == 1;
        await tx.CommitAsync(ct);
        return removed ? PayrollWriteOutcome.Done : PayrollWriteOutcome.NotFound;
    }

    /// <summary>Discards a Draft run (nothing was posted).</summary>
    public async Task<PayrollWriteOutcome> DeleteRunAsync(CloudTenantScope scope, Guid actorId, Guid runId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var status = await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct);
        if (status != "Draft")
        {
            await tx.RollbackAsync(ct);
            return status is null ? PayrollWriteOutcome.NotFound : PayrollWriteOutcome.NotDraft;
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM payroll_runs WHERE id = $1", connection, tx))
        {
            delete.Parameters.AddWithValue(runId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await PostgresEmployeeStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, runId, "payroll.discarded", new { runId }, ct,
            entityType: "payroll-run");
        await tx.CommitAsync(ct);
        return PayrollWriteOutcome.Done;
    }

    /// <summary>
    /// Pays a Draft run from a treasury account on a date: every payslip posts on the employee's account, settles the goods
    /// on the linked customer's account and takes its net out of the treasury; the run becomes Paid. All or nothing.
    /// </summary>
    public async Task<PayrollWriteOutcome> PayRunAsync(
        CloudTenantScope scope, Guid actorId, Guid runId, Guid accountId, DateOnly paidOn, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        async Task<PayrollWriteOutcome> Refuse(PayrollWriteOutcome outcome)
        {
            await tx.RollbackAsync(ct);
            return outcome;
        }

        var status = await LockRunStatusAsync(connection, tx, runId, scope.BranchId!.Value, ct);
        if (status is null) return await Refuse(PayrollWriteOutcome.NotFound);
        if (status != "Draft") return await Refuse(PayrollWriteOutcome.NotDraft);

        await using (var account = new NpgsqlCommand("SELECT is_active FROM treasury_accounts WHERE id = $1", connection, tx))
        {
            account.Parameters.AddWithValue(accountId);
            switch (await account.ExecuteScalarAsync(ct))
            {
                case null: return await Refuse(PayrollWriteOutcome.AccountNotFound);
                case false: return await Refuse(PayrollWriteOutcome.AccountInactive);
            }
        }

        var detail = (await ReadDetailAsync(connection, tx, scope, runId, ct))!;
        if (detail.Payslips.Count == 0)
        {
            return await Refuse(PayrollWriteOutcome.NothingToPay);
        }

        var org = scope.OrganizationId;
        var label = $"liquidación {detail.Run.RunNumber} ({PeriodText(detail.Run.PeriodFrom, detail.Run.PeriodTo)})";
        foreach (var slip in detail.Payslips)
        {
            var employee = AccountParty.Employee(slip.EmployeeId);
            var customerId = await LinkedCustomerAsync(connection, tx, slip.EmployeeId, ct);

            // The goods to settle: never more than the linked customer still owes now.
            var owed = customerId is { } customer ? Math.Max(await PostgresEmployeeStore.BalanceAsync(connection, tx, AccountParty.Customer(customer), ct), 0m) : 0m;
            var payslip = ToPayslip(slip.Lines, Math.Min(slip.PurchasesAmount, owed), slip.PurchasesDiscountPercent);

            // An Adjustment names its direction: on the employee's account a deduction is a Debit (the business owes it less).
            async Task Post(AccountParty party, AccountMovementKind kind, decimal amount, string concept, string source, AccountDirection? requested = null)
            {
                if (amount <= 0m)
                {
                    return;
                }

                PartyAccountRules.TryResolveDirection(party.Kind, kind, requested, out var direction, out _);
                await PostgresCurrentAccountStore.InsertAsync(connection, tx, org, party,
                    new NewAccountMovement(Guid.NewGuid(), kind, direction, amount, paidOn, null, $"L{detail.Run.RunNumber}", concept, actorId),
                    null, ct, source, slip.PayslipId);
            }

            await Post(employee, AccountMovementKind.Invoice, payslip.Earnings, $"Sueldo, {label}", EarningsSource);
            await Post(employee, AccountMovementKind.Adjustment, payslip.OtherDeductions, $"Descuentos, {label}", DeductionsSource, AccountDirection.Debit);
            await Post(employee, AccountMovementKind.Adjustment, payslip.Purchases.Deducted, $"Compras de mercadería descontadas, {label}",
                GoodsSource, AccountDirection.Debit);
            await Post(employee, AccountMovementKind.Payment, payslip.NetPaid, $"Pago de sueldo, {label}", PaymentSource);
            if (customerId is { } buyer)
            {
                var buyerAccount = AccountParty.Customer(buyer);
                await Post(buyerAccount, AccountMovementKind.Payment, payslip.Purchases.Deducted, $"Descontado del sueldo, {label}", GoodsSource);
                await Post(buyerAccount, AccountMovementKind.CreditNote, payslip.Purchases.Discount,
                    $"Bonificación al personal ({payslip.Purchases.DiscountPercent:0.##} %), {label}", GoodsDiscountSource);
            }

            if (payslip.NetPaid > 0m)
            {
                await PostgresTreasuryStore.InsertAsync(
                    connection, tx, org, Guid.NewGuid(), accountId, "SalaryPayment", "Out", payslip.NetPaid, DateTimeOffset.UtcNow, paidOn,
                    $"Sueldo de {slip.EmployeeName}, {label}", $"L{detail.Run.RunNumber}", null, PaymentSource, slip.PayslipId, null, actorId, ct);
            }
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE payroll_runs SET status = 'Paid', paid_on = $2, paid_at_utc = now(), payment_account_id = $3 WHERE id = $1", connection, tx))
        {
            update.Parameters.AddWithValue(runId);
            update.Parameters.AddWithValue(paidOn);
            update.Parameters.AddWithValue(accountId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await PostgresEmployeeStore.AuditAsync(connection, tx, org, actorId, runId, "payroll.paid", new
        {
            detail.Run.RunNumber,
            detail.Run.PeriodFrom,
            detail.Run.PeriodTo,
            paidOn,
            accountId,
            payslips = detail.Payslips.Select(p => new { p.EmployeeId, p.Earnings, p.Deductions, p.PurchasesDeducted, p.PurchasesDiscount, p.NetPaid }),
            total = detail.Payslips.Sum(p => p.NetPaid),
        }, ct, entityType: "payroll-run");
        await tx.CommitAsync(ct);
        return PayrollWriteOutcome.Done;
    }

    private static async Task<Guid?> LinkedCustomerAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT customer_id FROM employees WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(employeeId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }
}
