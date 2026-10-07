using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Customers;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// One employee (PRD 9.19). <see cref="CustomerId"/>: the linked customer the POS sells goods to on current account, whose
/// debt the payroll deducts from the salary; null while the employee does not take goods. <see cref="Balance"/>: what the
/// business owes the employee on its account (negative: what the employee owes).
/// </summary>
public sealed record EmployeeRecord(
    Guid Id,
    Guid BranchId,
    string? BranchName,
    int FileNumber,
    string FirstName,
    string LastName,
    string? DocumentNumber,
    string? Cuil,
    Guid? RoleId,
    string? RoleName,
    string? Phone,
    string? Email,
    string? Address,
    DateOnly? HireDate,
    DateOnly? TerminationDate,
    string PayFrequency,
    decimal BaseSalary,
    Guid? CustomerId,
    string? Notes,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    decimal Balance = 0m,
    decimal PurchasesOwed = 0m)
{
    public string FullName => $"{LastName}, {FirstName}";
}

/// <summary>What the staff form writes. <see cref="FileNumber"/> null on create: the next one of the organization.</summary>
public sealed record EmployeeInput(
    Guid BranchId,
    int? FileNumber,
    string FirstName,
    string LastName,
    string? DocumentNumber,
    string? Cuil,
    Guid? RoleId,
    string? Phone,
    string? Email,
    string? Address,
    DateOnly? HireDate,
    string PayFrequency,
    decimal BaseSalary,
    string? Notes);

public enum EmployeeWriteOutcome
{
    Done,
    NotFound,
    BranchNotFound,
    RoleNotFound,
    FileNumberInUse,
    AccountNotFound,
    AccountInactive,
}

/// <summary>
/// The staff of the organization (`employees`, 0050), by branch: their file, their agreed pay, their link to a customer
/// for the goods they take, and the advances handed out to them (money out of the treasury, a debit on their account).
/// Every write is audited; an employee is never deleted, only deactivated.
/// </summary>
public sealed class PostgresEmployeeStore
{
    public const string AdvanceSourceType = "EmployeeAdvance";

    private const string Select = """
        SELECT e.id, e.branch_id, b.name, e.file_number, e.first_name, e.last_name, e.document_number, e.cuil, e.role_id, r.name,
               e.phone, e.email, e.address, e.hire_date, e.termination_date, e.pay_frequency, e.base_salary, e.customer_id, e.notes,
               e.is_active, e.created_at_utc, e.updated_at_utc
        FROM employees e
        LEFT JOIN branches b ON b.id = e.branch_id
        LEFT JOIN employee_roles r ON r.organization_id = e.organization_id AND r.id = e.role_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresCustomerStore _customers;

    public PostgresEmployeeStore(NpgsqlDataSource dataSource, PostgresCustomerStore customers)
    {
        _dataSource = dataSource;
        _customers = customers;
    }

    private static EmployeeRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateOnly>(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateOnly>(14), r.GetString(15), r.GetDecimal(16), r.IsDBNull(17) ? null : r.GetGuid(17),
        r.IsDBNull(18) ? null : r.GetString(18), r.GetBoolean(19), r.GetFieldValue<DateTimeOffset>(20), r.GetFieldValue<DateTimeOffset>(21));

    /// <summary>The employees (of one branch, or all), active first then by last name, with their balances.</summary>
    public async Task<IReadOnlyList<EmployeeRecord>> ListAsync(CloudTenantScope scope, Guid? branchId, bool includeInactive, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var employees = await ReadAsync(connection, tx,
            "WHERE ($1::uuid IS NULL OR e.branch_id = $1) AND ($2 OR e.is_active) ORDER BY e.is_active DESC, lower(e.last_name), lower(e.first_name)",
            ct, (NpgsqlTypes.NpgsqlDbType.Uuid, branchId), (NpgsqlTypes.NpgsqlDbType.Boolean, includeInactive));
        var withBalances = await WithBalancesAsync(connection, tx, employees, ct);
        await tx.CommitAsync(ct);
        return withBalances;
    }

    public async Task<EmployeeRecord?> GetAsync(CloudTenantScope scope, Guid employeeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var employee = (await ReadAsync(connection, tx, "WHERE e.id = $1", ct, (NpgsqlTypes.NpgsqlDbType.Uuid, employeeId))).SingleOrDefault();
        var result = employee is null ? null : (await WithBalancesAsync(connection, tx, [employee], ct))[0];
        await tx.CommitAsync(ct);
        return result;
    }

    internal static async Task<List<EmployeeRecord>> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string where, CancellationToken ct, params (NpgsqlTypes.NpgsqlDbType Type, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand($"{Select} {where}", connection, tx);
        foreach (var (type, value) in parameters)
        {
            cmd.Parameters.AddWithValue(type, value ?? DBNull.Value);
        }

        var employees = new List<EmployeeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            employees.Add(Read(reader));
        }

        return employees;
    }

    /// <summary>Each employee with its account balance and what its linked customer owes (the goods to deduct).</summary>
    private static async Task<IReadOnlyList<EmployeeRecord>> WithBalancesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<EmployeeRecord> employees, CancellationToken ct)
    {
        var result = new List<EmployeeRecord>(employees.Count);
        foreach (var employee in employees)
        {
            result.Add(employee with
            {
                Balance = await BalanceAsync(connection, tx, AccountParty.Employee(employee.Id), ct),
                PurchasesOwed = employee.CustomerId is { } customer
                    ? Math.Max(await BalanceAsync(connection, tx, AccountParty.Customer(customer), ct), 0m)
                    : 0m,
            });
        }

        return result;
    }

    /// <summary>The balance of a party's account, in the party's own terms (inside the caller's transaction).</summary>
    internal static async Task<decimal> BalanceAsync(NpgsqlConnection connection, NpgsqlTransaction tx, AccountParty party, CancellationToken ct)
    {
        var column = party.Kind switch
        {
            AccountPartyKind.Supplier => "supplier_id",
            AccountPartyKind.Customer => "customer_id",
            _ => "employee_id",
        };
        await using var cmd = new NpgsqlCommand(
            $"SELECT direction, amount FROM current_account_movements WHERE {column} = $1", connection, tx);
        cmd.Parameters.AddWithValue(party.Id);
        var balance = 0m;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            balance += PartyAccountRules.SignedAmount(party.Kind, Enum.Parse<AccountDirection>(reader.GetString(0)), reader.GetDecimal(1));
        }

        return balance;
    }

    private static async Task<EmployeeWriteOutcome?> CheckReferencesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, EmployeeInput input, CancellationToken ct)
    {
        await using (var branch = new NpgsqlCommand("SELECT 1 FROM branches WHERE id = $1", connection, tx))
        {
            branch.Parameters.AddWithValue(input.BranchId);
            if (await branch.ExecuteScalarAsync(ct) is null)
            {
                return EmployeeWriteOutcome.BranchNotFound;
            }
        }

        if (input.RoleId is { } roleId)
        {
            await using var role = new NpgsqlCommand("SELECT 1 FROM employee_roles WHERE id = $1", connection, tx);
            role.Parameters.AddWithValue(roleId);
            if (await role.ExecuteScalarAsync(ct) is null)
            {
                return EmployeeWriteOutcome.RoleNotFound;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates an employee with the next file number of the organization (or the given one). With
    /// <paramref name="takesGoods"/> it also gets its linked customer, so the POS can sell goods to it on account.
    /// </summary>
    public async Task<(EmployeeWriteOutcome Outcome, EmployeeRecord? Employee)> CreateAsync(
        CloudTenantScope scope, Guid actorId, EmployeeInput input, bool takesGoods, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
            if (await CheckReferencesAsync(connection, tx, input, ct) is { } refused)
            {
                await tx.RollbackAsync(ct);
                return (refused, null);
            }

            // One file number at a time per organization.
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('employee-file:' || $1::text))", connection, tx))
            {
                lockCmd.Parameters.AddWithValue(scope.OrganizationId);
                await lockCmd.ExecuteNonQueryAsync(ct);
            }

            var fileNumber = input.FileNumber;
            if (fileNumber is null)
            {
                await using var next = new NpgsqlCommand("SELECT COALESCE(MAX(file_number), 0) + 1 FROM employees", connection, tx);
                fileNumber = (int)(await next.ExecuteScalarAsync(ct))!;
            }
            else if (await FileNumberTakenAsync(connection, tx, fileNumber.Value, null, ct))
            {
                await tx.RollbackAsync(ct);
                return (EmployeeWriteOutcome.FileNumberInUse, null);
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO employees (organization_id, id, branch_id, file_number, first_name, last_name, document_number, cuil, role_id,
                                       phone, email, address, hire_date, pay_frequency, base_salary, notes)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
                """, connection, tx))
            {
                insert.Parameters.AddWithValue(scope.OrganizationId);
                insert.Parameters.AddWithValue(id);
                AddInput(insert, input, fileNumber.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await AuditAsync(connection, tx, scope.OrganizationId, actorId, id, "employee.created",
                new { fileNumber, input.FirstName, input.LastName, input.BranchId, input.RoleId, input.PayFrequency, input.BaseSalary }, ct);
            await tx.CommitAsync(ct);
        }

        if (takesGoods)
        {
            await LinkCustomerAsync(scope, actorId, id, ct);
        }

        return (EmployeeWriteOutcome.Done, await GetAsync(scope, id, ct));
    }

    private static void AddInput(NpgsqlCommand cmd, EmployeeInput input, int fileNumber)
    {
        cmd.Parameters.AddWithValue(input.BranchId);
        cmd.Parameters.AddWithValue(fileNumber);
        cmd.Parameters.AddWithValue(input.FirstName);
        cmd.Parameters.AddWithValue(input.LastName);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.DocumentNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Cuil ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)input.RoleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)input.HireDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue(input.PayFrequency);
        cmd.Parameters.AddWithValue(input.BaseSalary);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Notes ?? DBNull.Value);
    }

    private static async Task<bool> FileNumberTakenAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, int fileNumber, Guid? except, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM employees WHERE file_number = $1 AND ($2::uuid IS NULL OR id <> $2)", connection, tx);
        cmd.Parameters.AddWithValue(fileNumber);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)except ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<(EmployeeWriteOutcome Outcome, EmployeeRecord? Employee)> UpdateAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, EmployeeInput input, bool takesGoods, CancellationToken ct)
    {
        EmployeeRecord before;
        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
            var found = (await ReadAsync(connection, tx, "WHERE e.id = $1", ct, (NpgsqlTypes.NpgsqlDbType.Uuid, employeeId))).SingleOrDefault();
            if (found is null)
            {
                await tx.RollbackAsync(ct);
                return (EmployeeWriteOutcome.NotFound, null);
            }

            before = found;
            if (await CheckReferencesAsync(connection, tx, input, ct) is { } refused)
            {
                await tx.RollbackAsync(ct);
                return (refused, null);
            }

            var fileNumber = input.FileNumber ?? before.FileNumber;
            if (fileNumber != before.FileNumber && await FileNumberTakenAsync(connection, tx, fileNumber, employeeId, ct))
            {
                await tx.RollbackAsync(ct);
                return (EmployeeWriteOutcome.FileNumberInUse, null);
            }

            await using (var update = new NpgsqlCommand(
                """
                UPDATE employees SET branch_id = $1, file_number = $2, first_name = $3, last_name = $4, document_number = $5, cuil = $6,
                                     role_id = $7, phone = $8, email = $9, address = $10, hire_date = $11, pay_frequency = $12,
                                     base_salary = $13, notes = $14, updated_at_utc = now()
                WHERE id = $15
                """, connection, tx))
            {
                AddInput(update, input, fileNumber);
                update.Parameters.AddWithValue(employeeId);
                await update.ExecuteNonQueryAsync(ct);
            }

            await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.updated",
                new { fileNumber, input.FirstName, input.LastName, input.BranchId, input.RoleId, input.PayFrequency, input.BaseSalary }, ct,
                old: new { before.FileNumber, before.FirstName, before.LastName, before.BranchId, before.RoleId, before.PayFrequency, before.BaseSalary });
            await tx.CommitAsync(ct);
        }

        if (takesGoods && before.CustomerId is null)
        {
            await LinkCustomerAsync(scope, actorId, employeeId, ct);
        }

        return (EmployeeWriteOutcome.Done, await GetAsync(scope, employeeId, ct));
    }

    /// <summary>
    /// The customer the POS sells goods to on behalf of the employee ("Pérez, Juan (personal)"), created once and linked:
    /// a sale on its current account moves stock as any sale, and the payroll deducts its debt.
    /// </summary>
    private async Task LinkCustomerAsync(CloudTenantScope scope, Guid actorId, Guid employeeId, CancellationToken ct)
    {
        var employee = await GetAsync(scope, employeeId, ct);
        if (employee is null || employee.CustomerId is not null)
        {
            return;
        }

        var customerId = Guid.NewGuid();
        await _customers.CreateAsync(scope, new NewCustomer(
            customerId, CustomerKind.Retail, $"{employee.FullName} (personal)", null,
            employee.DocumentNumber is null ? TaxIdType.None : TaxIdType.Dni, employee.DocumentNumber, TaxCondition.ConsumidorFinal,
            employee.Phone, employee.Email, null, null, null, null, null, null, null, null, null,
            $"Cuenta de compras del empleado legajo {employee.FileNumber}: se descuenta del sueldo.", actorId),
            "org-user", actorId, ct);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using (var link = new NpgsqlCommand("UPDATE employees SET customer_id = $2, updated_at_utc = now() WHERE id = $1", connection, tx))
        {
            link.Parameters.AddWithValue(employeeId);
            link.Parameters.AddWithValue(customerId);
            await link.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.purchases_enabled", new { customerId }, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Deactivates (with its termination date) or reactivates an employee; it is never deleted.</summary>
    public async Task<EmployeeWriteOutcome> SetActiveAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, bool isActive, DateOnly? terminationDate, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using (var update = new NpgsqlCommand(
            "UPDATE employees SET is_active = $2, termination_date = $3, updated_at_utc = now() WHERE id = $1", connection, tx))
        {
            update.Parameters.AddWithValue(employeeId);
            update.Parameters.AddWithValue(isActive);
            update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, isActive ? DBNull.Value : (object?)terminationDate ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return EmployeeWriteOutcome.NotFound;
            }
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId,
            isActive ? "employee.reactivated" : "employee.deactivated", new { isActive, terminationDate }, ct);
        await tx.CommitAsync(ct);
        return EmployeeWriteOutcome.Done;
    }

    /// <summary>
    /// An advance (adelanto) handed to the employee: money Out of an active treasury account and a Payment (Debit) on the
    /// employee's account, both in one transaction and keyed by the advance id. The next payroll deducts it.
    /// </summary>
    public async Task<(EmployeeWriteOutcome Outcome, Guid? AdvanceId)> GiveAdvanceAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, decimal amount, DateOnly date, Guid accountId, string? concept, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var employee = (await ReadAsync(connection, tx, "WHERE e.id = $1", ct, (NpgsqlTypes.NpgsqlDbType.Uuid, employeeId))).SingleOrDefault();
        if (employee is null)
        {
            await tx.RollbackAsync(ct);
            return (EmployeeWriteOutcome.NotFound, null);
        }

        await using (var account = new NpgsqlCommand("SELECT is_active FROM treasury_accounts WHERE id = $1", connection, tx))
        {
            account.Parameters.AddWithValue(accountId);
            switch (await account.ExecuteScalarAsync(ct))
            {
                case null:
                    await tx.RollbackAsync(ct);
                    return (EmployeeWriteOutcome.AccountNotFound, null);
                case false:
                    await tx.RollbackAsync(ct);
                    return (EmployeeWriteOutcome.AccountInactive, null);
            }
        }

        var advanceId = Guid.NewGuid();
        var text = string.IsNullOrWhiteSpace(concept) ? $"Adelanto a {employee.FullName}" : $"Adelanto a {employee.FullName}: {concept.Trim()}";
        await PostgresTreasuryStore.InsertAsync(
            connection, tx, scope.OrganizationId, Guid.NewGuid(), accountId, "EmployeeAdvance", "Out", amount, DateTimeOffset.UtcNow, date, text,
            null, null, AdvanceSourceType, advanceId, null, actorId, ct);
        await PostgresCurrentAccountStore.InsertAsync(
            connection, tx, scope.OrganizationId, AccountParty.Employee(employeeId),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Payment, AccountDirection.Debit, amount, date, null, null,
                string.IsNullOrWhiteSpace(concept) ? "Adelanto" : $"Adelanto: {concept.Trim()}", actorId),
            null, ct, AdvanceSourceType, advanceId);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.advance_given",
            new { advanceId, amount, date, accountId, concept }, ct);
        await tx.CommitAsync(ct);
        return (EmployeeWriteOutcome.Done, advanceId);
    }

    internal static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid actorId, Guid entityId, string action, object detail,
        CancellationToken ct, object? old = null, string entityType = "employee") =>
        AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            "org-user", actorId, organizationId, entityType, entityId, action,
            old is null ? null : JsonSerializer.Serialize(old), JsonSerializer.Serialize(detail)), ct);
}

/// <summary>The organization's catalog of positions (`employee_roles`, "puestos"); audit entity "employee_role".</summary>
public sealed class PostgresEmployeeRoleStore : PostgresMasterDataStore
{
    public PostgresEmployeeRoleStore(NpgsqlDataSource dataSource) : base(dataSource, "employee_roles", "employee_role") { }
}
