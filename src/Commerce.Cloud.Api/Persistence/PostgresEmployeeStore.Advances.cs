using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// An advance handed out to an employee as the history reads it. Voided: its cash movement was voided or its debit
/// reversed (the reason is the cash void's, or the reversal's concept for one reversed on the account only).
/// <see cref="CorrectedByAdvanceId"/> / <see cref="CorrectsAdvanceId"/> link a corrected advance and the one that replaced it.
/// </summary>
public sealed record EmployeeAdvanceRecord(
    Guid AdvanceId,
    DateOnly Date,
    decimal Amount,
    Guid AccountId,
    string AccountName,
    string? Concept,
    bool IsVoided,
    string? VoidReason,
    DateTimeOffset? VoidedAtUtc,
    Guid? CorrectedByAdvanceId,
    Guid? CorrectsAdvanceId,
    DateTimeOffset CreatedAtUtc);

public enum AdvanceWriteOutcome
{
    Done,
    NotFound,
    /// <summary>The employee is in a Draft payroll, whose advances line already counted it.</summary>
    InPayrollDraft,
    /// <summary>A correction of an advance already voided.</summary>
    Voided,
    AccountNotFound,
    AccountInactive,
}

/// <summary>
/// owner feedback 2026-10-14, A2: the advances of an employee, listed by date, voided or corrected whole. An advance is two
/// movements keyed by its id: the cash it left (treasury, kind EmployeeAdvance, Out) and the debit on the employee's
/// account. Voiding voids the first (it stays, struck through with the reason, and counts in no balance) and reverses the
/// second, in one transaction; whichever side was already undone is left as it is, so a retry moves nothing and an advance
/// reversed on the account only (before this change) gets its cash voided too.
/// </summary>
public sealed partial class PostgresEmployeeStore
{
    /// <summary>The longest void or correction reason: with the "Corregido: " prefix it still fits a treasury void reason (200).</summary>
    public const int MaxAdvanceReasonLength = 150;

    private const string AdvanceDebitSql = """
        SELECT d.id, EXISTS (SELECT 1 FROM current_account_movements r WHERE r.reverses_movement_id = d.id),
               EXISTS (SELECT 1 FROM treasury_movements t JOIN treasury_movement_voids v ON v.organization_id = t.organization_id AND v.movement_id = t.id
                       WHERE t.source_type = 'EmployeeAdvance' AND t.source_id = d.source_id AND t.kind <> 'Reversal')
        FROM current_account_movements d
        WHERE d.employee_id = $1 AND d.source_type = 'EmployeeAdvance' AND d.source_id = $2 AND d.kind <> 'Reversal'
        """;

    /// <summary>The employee's advances dated from <paramref name="from"/> to <paramref name="to"/>, newest first. Null: no such employee.</summary>
    public async Task<IReadOnlyList<EmployeeAdvanceRecord>?> ListAdvancesAsync(
        CloudTenantScope scope, Guid employeeId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        await using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM employees WHERE id = $1)", connection, tx))
        {
            exists.Parameters.AddWithValue(employeeId);
            if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
            {
                return null;
            }
        }

        var advances = new List<EmployeeAdvanceRecord>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT d.source_id, t.business_date, t.amount, t.account_id, a.name, d.concept,
                   v.movement_id IS NOT NULL OR r.id IS NOT NULL, COALESCE(v.reason, r.concept), COALESCE(v.voided_at_utc, r.created_at_utc),
                   replacement.source_id, original.source_id, d.created_at_utc
            FROM current_account_movements d
            JOIN treasury_movements t ON t.organization_id = d.organization_id AND t.source_type = 'EmployeeAdvance'
                                     AND t.source_id = d.source_id AND t.kind <> 'Reversal'
            JOIN treasury_accounts a ON a.organization_id = t.organization_id AND a.id = t.account_id
            LEFT JOIN treasury_movement_voids v ON v.organization_id = t.organization_id AND v.movement_id = t.id
            LEFT JOIN current_account_movements r ON r.reverses_movement_id = d.id
            LEFT JOIN treasury_movements replacement ON replacement.organization_id = v.organization_id AND replacement.id = v.replacement_movement_id
            LEFT JOIN treasury_movement_voids corrected ON corrected.organization_id = t.organization_id AND corrected.replacement_movement_id = t.id
            LEFT JOIN treasury_movements original ON original.organization_id = corrected.organization_id AND original.id = corrected.movement_id
            WHERE d.employee_id = $1 AND d.source_type = 'EmployeeAdvance' AND d.kind <> 'Reversal'
              AND t.business_date BETWEEN $2 AND $3
            ORDER BY t.business_date DESC, d.created_at_utc DESC
            """, connection, tx);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            advances.Add(new EmployeeAdvanceRecord(
                reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetDecimal(2), reader.GetGuid(3), reader.GetString(4),
                ConceptOf(reader.GetString(5)), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return advances;
    }

    /// <summary>Voids the advance whole: its cash movement voided, its debit reversed (each one unless already done).</summary>
    public async Task<AdvanceWriteOutcome> VoidAdvanceAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, Guid advanceId, string reason, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        if (await LockAdvanceAsync(connection, tx, employeeId, advanceId, ct) is not var (debitId, reversed, cashVoided))
        {
            await tx.RollbackAsync(ct);
            return AdvanceWriteOutcome.NotFound;
        }

        if (reversed && cashVoided)
        {
            await tx.RollbackAsync(ct);
            return AdvanceWriteOutcome.Done;
        }

        if (!reversed && await InPayrollDraftAsync(connection, tx, employeeId, ct))
        {
            await tx.RollbackAsync(ct);
            return AdvanceWriteOutcome.InPayrollDraft;
        }

        await UndoAdvanceAsync(connection, tx, scope.OrganizationId, actorId, employeeId, advanceId, debitId, reversed, cashVoided,
            $"Anulación de adelanto: {reason}", reason, null, today, ct);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.advance_voided", new { advanceId, reason }, ct);
        await tx.CommitAsync(ct);
        return AdvanceWriteOutcome.Done;
    }

    /// <summary>
    /// Corrects an advance: the corrected one is handed out and the original voided, its cash void naming the corrected
    /// one's cash movement as its replacement, in one transaction. Returns the corrected advance's id.
    /// </summary>
    public async Task<(AdvanceWriteOutcome Outcome, Guid? AdvanceId)> CorrectAdvanceAsync(
        CloudTenantScope scope, Guid actorId, Guid employeeId, Guid advanceId, decimal amount, DateOnly date, Guid accountId,
        string? concept, string reason, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var debit = await LockAdvanceAsync(connection, tx, employeeId, advanceId, ct);
        var outcome = debit switch
        {
            null => AdvanceWriteOutcome.NotFound,
            { Reversed: true } or { CashVoided: true } => AdvanceWriteOutcome.Voided,
            _ when await InPayrollDraftAsync(connection, tx, employeeId, ct) => AdvanceWriteOutcome.InPayrollDraft,
            _ => await AccountRefusalAsync(connection, tx, accountId, ct) switch
            {
                EmployeeWriteOutcome.AccountNotFound => AdvanceWriteOutcome.AccountNotFound,
                EmployeeWriteOutcome.AccountInactive => AdvanceWriteOutcome.AccountInactive,
                _ => AdvanceWriteOutcome.Done,
            },
        };
        if (outcome != AdvanceWriteOutcome.Done)
        {
            await tx.RollbackAsync(ct);
            return (outcome, null);
        }

        var employee = (await ReadAsync(connection, tx, "WHERE e.id = $1", ct, (NpgsqlTypes.NpgsqlDbType.Uuid, employeeId))).Single();
        var (correctedId, correctedCash) = await InsertAdvanceAsync(
            connection, tx, scope.OrganizationId, actorId, employee, amount, date, accountId, concept, ct);
        await UndoAdvanceAsync(connection, tx, scope.OrganizationId, actorId, employeeId, advanceId, debit!.Value.DebitId, false, false,
            $"Corrección de adelanto: {reason}", $"Corregido: {reason}", correctedCash, today, ct);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, employeeId, "employee.advance_corrected",
            new { advanceId, correctedBy = correctedId, amount, date, accountId, concept, reason }, ct);
        await tx.CommitAsync(ct);
        return (AdvanceWriteOutcome.Done, correctedId);
    }

    /// <summary>
    /// The two movements of a new advance, keyed by its id: money Out of the account and a Payment (Debit) on the employee's
    /// account. Returns the advance id and its cash movement's id.
    /// </summary>
    private static async Task<(Guid AdvanceId, Guid CashMovementId)> InsertAdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid actorId, EmployeeRecord employee, decimal amount,
        DateOnly date, Guid accountId, string? concept, CancellationToken ct)
    {
        var (advanceId, cashMovementId) = (Guid.NewGuid(), Guid.NewGuid());
        var text = string.IsNullOrWhiteSpace(concept) ? $"Adelanto a {employee.FullName}" : $"Adelanto a {employee.FullName}: {concept.Trim()}";
        await PostgresTreasuryStore.InsertAsync(
            connection, tx, organizationId, cashMovementId, accountId, "EmployeeAdvance", "Out", amount, DateTimeOffset.UtcNow, date, text,
            null, null, AdvanceSourceType, advanceId, null, actorId, ct);
        await PostgresCurrentAccountStore.InsertAsync(
            connection, tx, organizationId, AccountParty.Employee(employee.Id),
            new NewAccountMovement(Guid.NewGuid(), AccountMovementKind.Payment, AccountDirection.Debit, amount, date, null, null,
                string.IsNullOrWhiteSpace(concept) ? "Adelanto" : $"Adelanto: {concept.Trim()}", actorId),
            null, ct, AdvanceSourceType, advanceId);
        return (advanceId, cashMovementId);
    }

    /// <summary>Reverses the debit and voids the cash of an advance, each one unless <paramref name="reversed"/> / <paramref name="cashVoided"/>.</summary>
    private static async Task UndoAdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid actorId, Guid employeeId, Guid advanceId, Guid debitId,
        bool reversed, bool cashVoided, string reversalConcept, string voidReason, Guid? replacement, DateOnly today, CancellationToken ct)
    {
        if (!reversed)
        {
            // The employee is locked (LockAdvanceAsync): nobody else reversed the debit in between.
            var result = await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, organizationId, AccountParty.Employee(employeeId), debitId, reversalConcept, null, today, actorId, ct);
            if (result.Outcome != ReverseMovementOutcome.Reversed)
            {
                throw new InvalidOperationException($"The debit of advance {advanceId} was not reversed: {result.Outcome}.");
            }
        }

        if (!cashVoided)
        {
            await PostgresTreasuryStore.VoidSourceMovementAsync(connection, tx, organizationId, AdvanceSourceType, advanceId, actorId, voidReason, replacement, ct);
        }
    }

    /// <summary>
    /// The advance's debit on the employee's account, with whether it was reversed and its cash voided; null when the
    /// employee has no such advance. The employee's row is locked first (the ledgers are append-only, their rows cannot be):
    /// two voids or corrections of its advances, a reinstatement or a final settlement never interleave.
    /// </summary>
    private static async Task<(Guid DebitId, bool Reversed, bool CashVoided)?> LockAdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, Guid advanceId, CancellationToken ct)
    {
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM employees WHERE id = $1 FOR UPDATE", connection, tx))
        {
            lockRow.Parameters.AddWithValue(employeeId);
            await lockRow.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(AdvanceDebitSql, connection, tx);
        cmd.Parameters.AddWithValue(employeeId);
        cmd.Parameters.AddWithValue(advanceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2)) : null;
    }

    /// <summary>Whether the employee is in a Draft payroll (its lines, advances included, were read when it was prepared).</summary>
    internal static async Task<bool> InPayrollDraftAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid employeeId, CancellationToken ct)
    {
        await using var draft = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM payslips p JOIN payroll_runs r ON r.organization_id = p.organization_id AND r.id = p.run_id
                           WHERE p.employee_id = $1 AND r.status = 'Draft')
            """, connection, tx);
        draft.Parameters.AddWithValue(employeeId);
        return (bool)(await draft.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Null when the treasury account exists and is active; otherwise why it cannot pay an advance.</summary>
    private static async Task<EmployeeWriteOutcome?> AccountRefusalAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using var account = new NpgsqlCommand("SELECT is_active FROM treasury_accounts WHERE id = $1", connection, tx);
        account.Parameters.AddWithValue(accountId);
        return await account.ExecuteScalarAsync(ct) switch
        {
            null => EmployeeWriteOutcome.AccountNotFound,
            false => EmployeeWriteOutcome.AccountInactive,
            _ => null,
        };
    }

    /// <summary>What the operator typed as the advance's concept, from its debit's ("Adelanto" or "Adelanto: …").</summary>
    private static string? ConceptOf(string debitConcept) =>
        debitConcept.StartsWith("Adelanto: ", StringComparison.Ordinal) ? debitConcept["Adelanto: ".Length..] : null;
}
