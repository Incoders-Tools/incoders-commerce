using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Payroll;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>An overtime record as the staff form sends it. A null <see cref="Multiplier"/>: the one the day proposes.</summary>
public sealed record NewOvertime(DateOnly WorkDate, decimal Hours, decimal? Multiplier, string? Note);

/// <summary>
/// One overtime record (`employee_overtime`, 0054) with its frozen amount and where it stands
/// (<see cref="OvertimeStatus"/>); <see cref="RunNumber"/> is the payroll run that took it, if any.
/// </summary>
public sealed record OvertimeRecord(
    Guid Id,
    Guid EmployeeId,
    DateOnly WorkDate,
    decimal Hours,
    decimal Multiplier,
    decimal HourlyRate,
    decimal Amount,
    string? Note,
    string Status,
    int? RunNumber,
    DateTimeOffset CreatedAtUtc);

public enum OvertimeWriteOutcome
{
    Done,
    NotFound,
    /// <summary>The employee has no overtime hourly rate to multiply.</summary>
    RateMissing,
    /// <summary>A payroll already took it (Draft or Paid): it is changed there, not here.</summary>
    Settled,
}

/// <summary>
/// employee-lifecycle E2: the overtime of an employee. Recorded against the employee when it happens, with its amount
/// frozen (hours x the employee's rate at that moment x the multiplier); <see cref="PostgresPayrollStore"/> takes the
/// pending ones into the next payroll.
/// </summary>
public sealed partial class PostgresEmployeeStore
{
    private const string OvertimeSelect = """
        SELECT o.id, o.employee_id, o.work_date, o.hours, o.multiplier, o.hourly_rate, o.amount, o.note,
               CASE WHEN o.payslip_id IS NULL THEN 'Pending' WHEN run.status = 'Paid' THEN 'Paid' ELSE 'InPayroll' END,
               run.run_number, o.created_at_utc
        FROM employee_overtime o
        LEFT JOIN payslips p ON p.id = o.payslip_id
        LEFT JOIN payroll_runs run ON run.organization_id = p.organization_id AND run.id = p.run_id
        """;

    private static OvertimeRecord ReadOvertime(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10));

    /// <summary>The employee's overtime, newest day first.</summary>
    public async Task<IReadOnlyList<OvertimeRecord>> ListOvertimeAsync(CloudTenantScope scope, Guid employeeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var records = await ReadOvertimeAsync(connection, tx, "WHERE o.employee_id = $1 ORDER BY o.work_date DESC, o.created_at_utc DESC", employeeId, ct);
        await tx.CommitAsync(ct);
        return records;
    }

    public async Task<(OvertimeWriteOutcome Outcome, OvertimeRecord? Overtime)> RecordOvertimeAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, NewOvertime input, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var employee = (await ReadAsync(connection, tx, "WHERE e.id = $1", ct, (NpgsqlTypes.NpgsqlDbType.Uuid, employeeId))).SingleOrDefault();
        if (employee is null || employee.OvertimeHourlyRate is not { } rate)
        {
            await tx.RollbackAsync(ct);
            return (employee is null ? OvertimeWriteOutcome.NotFound : OvertimeWriteOutcome.RateMissing, null);
        }

        var id = Guid.NewGuid();
        var multiplier = input.Multiplier ?? OvertimeRules.ProposedMultiplier(input.WorkDate);
        var amount = OvertimeRules.Amount(input.Hours, rate, multiplier);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO employee_overtime (organization_id, id, employee_id, work_date, hours, multiplier, hourly_rate, amount, note, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(scope.OrganizationId);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(employeeId);
            insert.Parameters.AddWithValue(input.WorkDate);
            insert.Parameters.AddWithValue(input.Hours);
            insert.Parameters.AddWithValue(multiplier);
            insert.Parameters.AddWithValue(rate);
            insert.Parameters.AddWithValue(amount);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.Note ?? DBNull.Value);
            insert.Parameters.AddWithValue(actorId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.overtime_recorded",
            new { overtimeId = id, input.WorkDate, input.Hours, multiplier, rate, amount, input.Note }, ct);
        var record = (await ReadOvertimeAsync(connection, tx, "WHERE o.id = $1", id, ct)).Single();
        await tx.CommitAsync(ct);
        return (OvertimeWriteOutcome.Done, record);
    }

    /// <summary>Deletes a record a payroll has not taken yet (a mistake); one in a payroll is changed in that payroll.</summary>
    public async Task<OvertimeWriteOutcome> DeleteOvertimeAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, Guid overtimeId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        bool taken;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT payslip_id IS NOT NULL FROM employee_overtime WHERE id = $1 AND employee_id = $2 FOR UPDATE", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(overtimeId);
            lockCmd.Parameters.AddWithValue(employeeId);
            if (await lockCmd.ExecuteScalarAsync(ct) is not bool found)
            {
                await tx.RollbackAsync(ct);
                return OvertimeWriteOutcome.NotFound;
            }

            taken = found;
        }

        if (taken)
        {
            await tx.RollbackAsync(ct);
            return OvertimeWriteOutcome.Settled;
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM employee_overtime WHERE id = $1", connection, tx))
        {
            delete.Parameters.AddWithValue(overtimeId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.overtime_deleted", new { overtimeId }, ct);
        await tx.CommitAsync(ct);
        return OvertimeWriteOutcome.Done;
    }

    private static async Task<List<OvertimeRecord>> ReadOvertimeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string where, Guid parameter, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"{OvertimeSelect} {where}", connection, tx);
        cmd.Parameters.AddWithValue(parameter);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var records = new List<OvertimeRecord>();
        while (await reader.ReadAsync(ct))
        {
            records.Add(ReadOvertime(reader));
        }

        return records;
    }
}
