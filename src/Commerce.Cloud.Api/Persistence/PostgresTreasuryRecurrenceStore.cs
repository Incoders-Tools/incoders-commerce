using System.Security.Cryptography;
using System.Text;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Treasury;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// A recurring treasury movement (0051): money In or Out of an account every N weeks, months or years from a start date,
/// until it ends. <see cref="NextOn"/>: the next date it will be recorded (null when it ended or is paused).
/// </summary>
public sealed record TreasuryRecurrenceRecord(
    Guid RecurrenceId,
    Guid AccountId,
    string AccountName,
    string Direction,
    decimal Amount,
    string Concept,
    string? DocumentReference,
    string Frequency,
    int Interval,
    DateOnly StartDate,
    string EndMode,
    DateOnly? EndDate,
    int? MaxOccurrences,
    bool IsActive,
    int OccurrencesRecorded,
    DateOnly? LastRecordedOn,
    DateOnly? NextOn,
    DateTimeOffset UpdatedAtUtc);

/// <summary>What the form writes. <see cref="IncludePastDates"/> on create: also record the dates since the start that already passed.</summary>
public sealed record TreasuryRecurrenceInput(
    Guid AccountId,
    string Direction,
    decimal Amount,
    string Concept,
    string? DocumentReference,
    string Frequency,
    int Interval,
    DateOnly StartDate,
    string EndMode,
    DateOnly? EndDate,
    int? MaxOccurrences,
    bool IncludePastDates);

public enum RecurrenceWriteOutcome
{
    Done,
    NotFound,
    AccountNotFound,
    AccountInactive,

    /// <summary>Frequency, interval or start date changed after dates were already recorded.</summary>
    ScheduleLocked,
}

/// <summary>
/// Recurring treasury movements (fixed expenses: electricity, gas, internet, rent...; or recurring income). Each date is
/// recorded as an ordinary manual movement (ManualIn / ManualOut) pointing at its recurrence, keyed by the recurrence and
/// the date, so <see cref="GenerateDueAsync"/> records it ONCE however often it runs: when the treasury is read and by the
/// daily job. A recorded occurrence is edited or voided like any manual movement (a voided one is not recorded again).
/// Editing a recurrence changes what comes next (amount, concept, account, end); its frequency, interval and start date
/// are fixed once a date was recorded (to change them, end it and create another). Pausing stops it; resuming records
/// from that day on (the dates while paused are skipped). Everything audited; nothing deleted.
/// </summary>
public sealed class PostgresTreasuryRecurrenceStore
{
    public const string SourceType = "TreasuryRecurrence";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTreasuryRecurrenceStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>The id of the occurrence of a recurrence on a date: the first 16 bytes of SHA-256("treasury-recurrence:{id}:{date}").</summary>
    public static Guid OccurrenceKey(Guid recurrenceId, DateOnly date) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"treasury-recurrence:{recurrenceId:D}:{date:yyyy-MM-dd}"))[..16]);

    private sealed record Row(
        Guid Id, Guid AccountId, string AccountName, bool AccountActive, string Direction, decimal Amount, string Concept, string? Reference,
        string Frequency, int Interval, DateOnly Start, string EndMode, DateOnly? EndDate, int? Max, bool IsActive, DateOnly GenerateFrom,
        Guid CreatedBy, DateTimeOffset UpdatedAtUtc)
    {
        public RecurrenceSchedule Schedule => new(Start, Frequency, Interval, EndMode, EndDate, Max);
    }

    private static async Task<List<Row>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string where, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT r.id, r.account_id, a.name, a.is_active, r.direction, r.amount, r.concept, r.document_reference, r.frequency,
                   r.interval_count, r.start_date, r.end_mode, r.end_date, r.max_occurrences, r.is_active, r.generate_from,
                   r.created_by_user_id, r.updated_at_utc
            FROM treasury_recurrences r
            JOIN treasury_accounts a ON a.organization_id = r.organization_id AND a.id = r.account_id
            {where}
            """, connection, tx);
        foreach (var arg in args)
        {
            cmd.Parameters.AddWithValue(arg);
        }

        var rows = new List<Row>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new Row(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetString(4), reader.GetDecimal(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetInt32(9),
                reader.GetFieldValue<DateOnly>(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateOnly>(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13), reader.GetBoolean(14), reader.GetFieldValue<DateOnly>(15), reader.GetGuid(16),
                reader.GetFieldValue<DateTimeOffset>(17)));
        }

        return rows;
    }

    /// <summary>
    /// Records every date due by <paramref name="today"/> of the organization's active recurrences (inside the caller's
    /// transaction and tenant scope). Idempotent: an already recorded date (even voided since) is not recorded again.
    /// Returns how many movements it added.
    /// </summary>
    internal static async Task<int> GenerateDueAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, DateOnly today, CancellationToken ct)
    {
        var added = 0;
        foreach (var row in await ReadAsync(connection, tx, "WHERE r.is_active AND a.is_active AND r.start_date <= $1", ct, today))
        {
            foreach (var date in row.Schedule.OccurrencesUntil(today).Where(date => date >= row.GenerateFrom))
            {
                // A recurring movement happens on its date: midday of the business day.
                var occurredAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-3)).ToUniversalTime();
                if (await PostgresTreasuryStore.InsertAsync(
                        connection, tx, organizationId, Guid.NewGuid(), row.AccountId, row.Direction == "In" ? "ManualIn" : "ManualOut",
                        row.Direction, row.Amount, occurredAt, date, row.Concept, row.Reference, null, SourceType, OccurrenceKey(row.Id, date),
                        null, row.CreatedBy, ct, recurrenceId: row.Id))
                {
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>The organization's recurrences (active first), after recording what is due by today.</summary>
    public async Task<IReadOnlyList<TreasuryRecurrenceRecord>> ListAsync(CloudTenantScope scope, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await GenerateDueAsync(connection, tx, scope.OrganizationId, today, ct);
        var rows = await ReadAsync(connection, tx, "ORDER BY r.is_active DESC, lower(r.concept)", ct);
        var records = new List<TreasuryRecurrenceRecord>(rows.Count);
        foreach (var row in rows)
        {
            records.Add(await ToRecordAsync(connection, tx, row, today, ct));
        }

        await tx.CommitAsync(ct);
        return records;
    }

    private static async Task<TreasuryRecurrenceRecord> ToRecordAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Row row, DateOnly today, CancellationToken ct)
    {
        int count;
        DateOnly? last;
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*)::integer, max(business_date) FROM treasury_movements WHERE recurrence_id = $1 AND source_type = $2", connection, tx))
        {
            cmd.Parameters.AddWithValue(row.Id);
            cmd.Parameters.AddWithValue(SourceType);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            count = reader.GetInt32(0);
            last = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
        }

        var after = (today < row.GenerateFrom ? row.GenerateFrom.AddDays(-1) : today);
        var next = row.IsActive ? row.Schedule.NextAfter(after) : null;
        return new TreasuryRecurrenceRecord(
            row.Id, row.AccountId, row.AccountName, row.Direction, row.Amount, row.Concept, row.Reference, row.Frequency, row.Interval, row.Start,
            row.EndMode, row.EndDate, row.Max, row.IsActive, count, last, next, row.UpdatedAtUtc);
    }

    private static async Task<RecurrenceWriteOutcome?> CheckAccountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT is_active FROM treasury_accounts WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(accountId);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            null => RecurrenceWriteOutcome.AccountNotFound,
            false => RecurrenceWriteOutcome.AccountInactive,
            _ => null,
        };
    }

    /// <summary>Creates a recurrence and records what is already due (from today, or from the start when asked).</summary>
    public async Task<(RecurrenceWriteOutcome Outcome, Guid? RecurrenceId)> CreateAsync(
        CloudTenantScope scope, Guid actorId, TreasuryRecurrenceInput input, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        if (await CheckAccountAsync(connection, tx, input.AccountId, ct) is { } refused)
        {
            await tx.RollbackAsync(ct);
            return (refused, null);
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO treasury_recurrences
                (organization_id, id, account_id, direction, amount, concept, document_reference, frequency, interval_count, start_date,
                 end_mode, end_date, max_occurrences, generate_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(scope.OrganizationId);
            insert.Parameters.AddWithValue(id);
            AddInput(insert, input);
            insert.Parameters.AddWithValue(input.IncludePastDates || input.StartDate > today ? input.StartDate : today);
            insert.Parameters.AddWithValue(actorId);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var recorded = await GenerateDueAsync(connection, tx, scope.OrganizationId, today, ct);
        await PostgresTreasuryStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-recurrence", id, "treasury.recurrence_created",
            new { input.AccountId, input.Direction, input.Amount, input.Concept, input.Frequency, input.Interval, input.StartDate, input.EndMode,
                  input.EndDate, input.MaxOccurrences, input.IncludePastDates, recorded }, ct);
        await tx.CommitAsync(ct);
        return (RecurrenceWriteOutcome.Done, id);
    }

    private static void AddInput(NpgsqlCommand cmd, TreasuryRecurrenceInput input)
    {
        cmd.Parameters.AddWithValue(input.AccountId);
        cmd.Parameters.AddWithValue(input.Direction);
        cmd.Parameters.AddWithValue(input.Amount);
        cmd.Parameters.AddWithValue(input.Concept);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)input.DocumentReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue(input.Frequency);
        cmd.Parameters.AddWithValue(input.Interval);
        cmd.Parameters.AddWithValue(input.StartDate);
        cmd.Parameters.AddWithValue(input.EndMode);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)input.EndDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer, (object?)input.MaxOccurrences ?? DBNull.Value);
    }

    /// <summary>
    /// Changes what comes next: amount, concept, reference, account and end. The frequency, interval and start date only
    /// change while no date was recorded yet.
    /// </summary>
    public async Task<RecurrenceWriteOutcome> UpdateAsync(
        CloudTenantScope scope, Guid actorId, Guid recurrenceId, TreasuryRecurrenceInput input, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var before = (await ReadAsync(connection, tx, "WHERE r.id = $1 FOR UPDATE OF r", ct, recurrenceId)).SingleOrDefault();
        if (before is null)
        {
            await tx.RollbackAsync(ct);
            return RecurrenceWriteOutcome.NotFound;
        }

        if (input.AccountId != before.AccountId && await CheckAccountAsync(connection, tx, input.AccountId, ct) is { } refused)
        {
            await tx.RollbackAsync(ct);
            return refused;
        }

        var scheduleChanged = input.Frequency != before.Frequency || input.Interval != before.Interval || input.StartDate != before.Start;
        if (scheduleChanged && (await ToRecordAsync(connection, tx, before, today, ct)).OccurrencesRecorded > 0)
        {
            await tx.RollbackAsync(ct);
            return RecurrenceWriteOutcome.ScheduleLocked;
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE treasury_recurrences
            SET account_id = $1, direction = $2, amount = $3, concept = $4, document_reference = $5, frequency = $6, interval_count = $7,
                start_date = $8, end_mode = $9, end_date = $10, max_occurrences = $11, updated_at_utc = now()
            WHERE id = $12
            """, connection, tx))
        {
            AddInput(update, input);
            update.Parameters.AddWithValue(recurrenceId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await PostgresTreasuryStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-recurrence", recurrenceId, "treasury.recurrence_updated",
            new { input.AccountId, input.Direction, input.Amount, input.Concept, input.Frequency, input.Interval, input.StartDate, input.EndMode, input.EndDate, input.MaxOccurrences },
            ct, old: new { before.AccountId, before.Direction, before.Amount, before.Concept, before.Frequency, before.Interval, before.Start, before.EndMode, before.EndDate, before.Max });
        await GenerateDueAsync(connection, tx, scope.OrganizationId, today, ct);
        await tx.CommitAsync(ct);
        return RecurrenceWriteOutcome.Done;
    }

    /// <summary>Pauses or resumes a recurrence; resuming records from that day on (the dates while paused are skipped).</summary>
    public async Task<RecurrenceWriteOutcome> SetActiveAsync(
        CloudTenantScope scope, Guid actorId, Guid recurrenceId, bool isActive, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE treasury_recurrences
            SET is_active = $2, generate_from = CASE WHEN $2 AND NOT is_active THEN GREATEST(generate_from, $3) ELSE generate_from END,
                updated_at_utc = now()
            WHERE id = $1
            """, connection, tx))
        {
            update.Parameters.AddWithValue(recurrenceId);
            update.Parameters.AddWithValue(isActive);
            update.Parameters.AddWithValue(today);
            if (await update.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return RecurrenceWriteOutcome.NotFound;
            }
        }

        await PostgresTreasuryStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-recurrence", recurrenceId,
            isActive ? "treasury.recurrence_resumed" : "treasury.recurrence_paused", new { isActive }, ct);
        await GenerateDueAsync(connection, tx, scope.OrganizationId, today, ct);
        await tx.CommitAsync(ct);
        return RecurrenceWriteOutcome.Done;
    }

    /// <summary>The daily job: records what is due for one organization (its own transaction and tenant scope).</summary>
    public async Task<int> GenerateDueForOrganizationAsync(Guid organizationId, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, organizationId, null, ct);
        var added = await GenerateDueAsync(connection, tx, organizationId, today, ct);
        await tx.CommitAsync(ct);
        return added;
    }
}
