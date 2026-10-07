using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store of the current accounts of suppliers and customers (`current_account_movements`, append-only:
/// the application role can only SELECT and INSERT). Every method opens its own transaction with `set_config` first;
/// the two writes (register, reverse) add their audit row in the same transaction. A cross-organization party or
/// movement is invisible under RLS (null / NotFound). The balance is never stored: it is derived from the movements by
/// the pure rules in <see cref="PartyAccountRules"/> (supplier: what the business owes; customer: what it is owed).
/// <para>
/// A movement can name the operation that produced it (`source_type`, `source_id`: a delivered order, a POS sale on
/// account); one source posts at most once per party, so a retried projection never charges twice.
/// </para>
/// </summary>
public sealed class PostgresCurrentAccountStore
{
    private const string Columns =
        "id, supplier_id, kind, direction, amount, occurred_on, due_on, document_reference, concept, reverses_movement_id, created_at_utc, created_by_user_id, customer_id, employee_id";

    /// <summary>
    /// Ledger order among movements of the same date written in the same transaction (same `created_at_utc`, e.g. a sale
    /// charged and paid at the counter): what increases the debt first, then what reduces it, reversals last, so the
    /// statement reads "sale, payment", never the other way round.
    /// </summary>
    private const string LedgerTieBreak =
        "CASE WHEN kind = 'Reversal' THEN 2 WHEN kind IN ('OpeningBalance', 'Invoice', 'DebitNote') THEN 0 ELSE 1 END";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresCurrentAccountStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static AccountMovementRecord Read(NpgsqlDataReader r) => new(
        Id: r.GetGuid(0),
        SupplierId: r.IsDBNull(1) ? null : r.GetGuid(1),
        Kind: Enum.Parse<AccountMovementKind>(r.GetString(2)),
        Direction: Enum.Parse<AccountDirection>(r.GetString(3)),
        Amount: r.GetDecimal(4),
        OccurredOn: r.GetFieldValue<DateOnly>(5),
        DueOn: r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6),
        DocumentReference: r.IsDBNull(7) ? null : r.GetString(7),
        Concept: r.GetString(8),
        ReversesMovementId: r.IsDBNull(9) ? null : r.GetGuid(9),
        CreatedAtUtc: r.GetFieldValue<DateTimeOffset>(10),
        CreatedByUserId: r.GetGuid(11),
        CustomerId: r.IsDBNull(12) ? null : r.GetGuid(12),
        EmployeeId: r.IsDBNull(13) ? null : r.GetGuid(13));

    private static string PartyColumn(AccountParty party) => party.Kind switch
    {
        AccountPartyKind.Supplier => "supplier_id",
        AccountPartyKind.Customer => "customer_id",
        _ => "employee_id",
    };

    /// <summary>Whether the party exists in the scope, and its payment terms in days (a customer without its own takes the organization's default).</summary>
    private static async Task<(bool Exists, int? PaymentTermsDays)> FindPartyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, AccountParty party, CancellationToken ct)
    {
        var sql = party.Kind == AccountPartyKind.Employee
            // An employee's movements have no due date.
            ? "SELECT NULL::integer FROM employees WHERE id = $1"
            : party.Kind == AccountPartyKind.Supplier
            ? "SELECT payment_terms_days FROM suppliers WHERE id = $1"
            // A customer's terms: its own days, else the organization's default (PaymentTerms).
            : """
              SELECT COALESCE(c.payment_terms_days, o.default_customer_payment_terms_days)::integer
              FROM customers c JOIN organizations o ON o.id = c.organization_id WHERE c.id = $1
              """;
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue(party.Id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (true, reader.IsDBNull(0) ? null : reader.GetInt32(0))
            : (false, null);
    }

    /// <summary>Appends a supplier movement inside the caller's transaction (the goods reception flow).</summary>
    internal static Task<AccountMovementRecord> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid supplierId,
        NewAccountMovement movement, Guid? reversesMovementId, CancellationToken ct) =>
        InsertAsync(connection, tx, organizationId, AccountParty.Supplier(supplierId), movement, reversesMovementId, ct);

    /// <summary>
    /// Appends a movement inside the caller's transaction (tenant scope already applied). With a source
    /// (<paramref name="sourceType"/>, <paramref name="sourceId"/>) a second posting of the same source for the party is
    /// a no-op and returns null.
    /// </summary>
    internal static async Task<AccountMovementRecord?> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, AccountParty party,
        NewAccountMovement movement, Guid? reversesMovementId, CancellationToken ct, string? sourceType = null, Guid? sourceId = null)
    {
        var onConflict = sourceType is null
            ? string.Empty
            : "ON CONFLICT (organization_id, party_id, source_type, source_id) WHERE source_type IS NOT NULL DO NOTHING";
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO current_account_movements
                (id, organization_id, party_kind, party_id, {PartyColumn(party)}, kind, direction, amount, occurred_on, due_on,
                 document_reference, concept, reverses_movement_id, created_by_user_id, source_type, source_id)
            VALUES ($1, $2, $3, $4, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            {onConflict}
            RETURNING {Columns}
            """, connection, tx);
        cmd.Parameters.AddWithValue(movement.Id);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(party.Kind.ToString());
        cmd.Parameters.AddWithValue(party.Id);
        cmd.Parameters.AddWithValue(movement.Kind.ToString());
        cmd.Parameters.AddWithValue(movement.Direction.ToString());
        cmd.Parameters.AddWithValue(movement.Amount);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, movement.OccurredOn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)movement.DueOn ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)movement.DocumentReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue(movement.Concept);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)reversesMovementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(movement.CreatedByUserId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)sourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)sourceId ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>The movement a source posted on the party's account (not its reversal), or null.</summary>
    internal static async Task<AccountMovementRecord?> FindBySourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, AccountParty party, string sourceType, Guid sourceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE party_id = $1 AND source_type = $2 AND source_id = $3",
            connection, tx);
        cmd.Parameters.AddWithValue(party.Id);
        cmd.Parameters.AddWithValue(sourceType);
        cmd.Parameters.AddWithValue(sourceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    internal static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, string actorKind, Guid actorId,
        Guid supplierId, string action, AccountMovementRecord movement, CancellationToken ct) =>
        AuditAsync(connection, tx, scope.OrganizationId, actorKind, actorId, AccountParty.Supplier(supplierId), action, movement, ct);

    internal static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, string actorKind, Guid actorId,
        AccountParty party, string action, AccountMovementRecord movement, CancellationToken ct) =>
        AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, organizationId, party.EntityType, party.Id, action, OldValueJson: null,
                NewValueJson:
                $$"""{"movementId":"{{movement.Id}}","kind":"{{movement.Kind}}","direction":"{{movement.Direction}}","amount":{{movement.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""),
            ct);

    /// <summary>Appends a movement. Null when the party is not visible in the scope's organization.</summary>
    public async Task<AccountMovementRecord?> RegisterAsync(
        CloudTenantScope scope, AccountParty party, NewAccountMovement movement, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var (exists, terms) = await FindPartyAsync(connection, tx, party, ct);
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var due = movement.DueOn ?? CurrentAccountRules.DefaultDueOn(movement.Kind, movement.OccurredOn, terms);
        var record = (await InsertAsync(connection, tx, scope.OrganizationId, party, movement with { DueOn = due }, null, ct))!;
        await AuditAsync(connection, tx, scope.OrganizationId, actorKind, actorId, party, $"{party.EntityType}.movement_registered", record, ct);

        await tx.CommitAsync(ct);
        return record;
    }

    public Task<AccountMovementRecord?> RegisterAsync(
        CloudTenantScope scope, Guid supplierId, NewAccountMovement movement, string actorKind, Guid actorId, CancellationToken ct) =>
        RegisterAsync(scope, AccountParty.Supplier(supplierId), movement, actorKind, actorId, ct);

    /// <summary>
    /// Reverses a movement of the party with a compensating Reversal (opposite direction, same amount, same document
    /// reference). A movement is reversed at most once (partial unique index) and a Reversal cannot be reversed.
    /// `OccurredOn` null takes today (Argentina time) or the original's date when that is later.
    /// </summary>
    public async Task<ReverseMovementResult> ReverseAsync(
        CloudTenantScope scope, AccountParty party, Guid movementId, string? concept, DateOnly? occurredOn, DateOnly today,
        string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var result = await ReverseWithinAsync(connection, tx, scope.OrganizationId, party, movementId, concept, occurredOn, today, actorId, ct);
        if (result.Outcome != ReverseMovementOutcome.Reversed)
        {
            await tx.RollbackAsync(ct);
            return result;
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorKind, actorId, party, $"{party.EntityType}.movement_reversed", result.Reversal!, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<ReverseMovementResult> ReverseAsync(
        CloudTenantScope scope, Guid supplierId, Guid movementId, string? concept, DateOnly? occurredOn, DateOnly today,
        string actorKind, Guid actorId, CancellationToken ct) =>
        ReverseAsync(scope, AccountParty.Supplier(supplierId), movementId, concept, occurredOn, today, actorKind, actorId, ct);

    internal static Task<ReverseMovementResult> ReverseWithinAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid supplierId, Guid movementId,
        string? concept, DateOnly? occurredOn, DateOnly today, Guid actorId, CancellationToken ct) =>
        ReverseWithinAsync(connection, tx, organizationId, AccountParty.Supplier(supplierId), movementId, concept, occurredOn, today, actorId, ct);

    /// <summary>
    /// The reversal itself, inside the CALLER's transaction (tenant scope already applied; the caller commits or rolls
    /// back and writes the audit row). Used directly by the goods reception void and the POS sale void so the account
    /// and the rest come back in one transaction. On any outcome other than Reversed nothing was written, except that a
    /// unique violation (AlreadyReversed) leaves the transaction aborted: the caller must roll back (or use a savepoint).
    /// </summary>
    internal static async Task<ReverseMovementResult> ReverseWithinAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, AccountParty party, Guid movementId,
        string? concept, DateOnly? occurredOn, DateOnly today, Guid actorId, CancellationToken ct)
    {
        AccountMovementRecord? original = null;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE id = $1 AND {PartyColumn(party)} = $2", connection, tx))
        {
            cmd.Parameters.AddWithValue(movementId);
            cmd.Parameters.AddWithValue(party.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                original = Read(reader);
            }
        }

        if (original is null)
        {
            return new ReverseMovementResult(ReverseMovementOutcome.NotFound);
        }

        if (original.Kind == AccountMovementKind.Reversal)
        {
            return new ReverseMovementResult(ReverseMovementOutcome.NotReversible);
        }

        var date = occurredOn ?? (today > original.OccurredOn ? today : original.OccurredOn);
        if (date < original.OccurredOn)
        {
            return new ReverseMovementResult(ReverseMovementOutcome.BeforeOriginal);
        }

        var reversal = new NewAccountMovement(
            Guid.NewGuid(), AccountMovementKind.Reversal, CurrentAccountRules.Opposite(original.Direction), original.Amount,
            date, null, original.DocumentReference,
            string.IsNullOrWhiteSpace(concept) ? $"Reversal: {original.Concept}" : concept.Trim(), actorId);

        try
        {
            var record = await InsertAsync(connection, tx, organizationId, party, reversal, original.Id, ct);
            return new ReverseMovementResult(ReverseMovementOutcome.Reversed, record);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "current_account_movements_one_reversal_uk")
        {
            return new ReverseMovementResult(ReverseMovementOutcome.AlreadyReversed);
        }
    }

    /// <summary>
    /// Every movement of the party in ledger order (date, then registration), or null when the party is not visible.
    /// The statement and the summary are derived from this list by the caller.
    /// </summary>
    public async Task<IReadOnlyList<AccountMovementRecord>?> ListMovementsAsync(CloudTenantScope scope, AccountParty party, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var (exists, _) = await FindPartyAsync(connection, tx, party, ct);
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var results = new List<AccountMovementRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE {PartyColumn(party)} = $1 ORDER BY occurred_on, created_at_utc, {LedgerTieBreak}, id",
            connection, tx))
        {
            cmd.Parameters.AddWithValue(party.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    public Task<IReadOnlyList<AccountMovementRecord>?> ListMovementsAsync(CloudTenantScope scope, Guid supplierId, CancellationToken ct) =>
        ListMovementsAsync(scope, AccountParty.Supplier(supplierId), ct);

    /// <summary>Balance and overdue, as of a date, of every supplier of the organization that has movements.</summary>
    public async Task<IReadOnlyList<SupplierAccountBalance>> BalancesAsync(CloudTenantScope scope, DateOnly asOf, CancellationToken ct) =>
        [.. (await PartyBalancesAsync(scope, AccountPartyKind.Supplier, asOf, ct))
            .Select(balance => new SupplierAccountBalance(balance.PartyId, balance.Summary.Balance, balance.Summary.Overdue))];

    /// <summary>Balance (what each customer owes) and overdue, as of a date, of every customer that has movements.</summary>
    public async Task<IReadOnlyList<CustomerAccountBalance>> CustomerBalancesAsync(CloudTenantScope scope, DateOnly asOf, CancellationToken ct) =>
        [.. (await PartyBalancesAsync(scope, AccountPartyKind.Customer, asOf, ct))
            .Select(balance => new CustomerAccountBalance(balance.PartyId, balance.Summary.Balance, balance.Summary.Overdue))];

    /// <summary>Balance (what the business owes each employee; negative: what the employee owes) of every employee with movements.</summary>
    public async Task<IReadOnlyList<EmployeeAccountBalance>> EmployeeBalancesAsync(CloudTenantScope scope, DateOnly asOf, CancellationToken ct) =>
        [.. (await PartyBalancesAsync(scope, AccountPartyKind.Employee, asOf, ct))
            .Select(balance => new EmployeeAccountBalance(balance.PartyId, balance.Summary.Balance))];

    private async Task<IReadOnlyList<(Guid PartyId, AccountSummary Summary)>> PartyBalancesAsync(
        CloudTenantScope scope, AccountPartyKind kind, DateOnly asOf, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var byParty = new Dictionary<Guid, List<AccountMovementFact>>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE party_kind = $1 ORDER BY party_id, occurred_on, created_at_utc, {LedgerTieBreak}, id",
            connection, tx))
        {
            cmd.Parameters.AddWithValue(kind.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var movement = Read(reader);
                if (!byParty.TryGetValue(movement.Party.Id, out var list))
                {
                    byParty[movement.Party.Id] = list = [];
                }

                list.Add(movement.ToFact());
            }
        }

        await tx.CommitAsync(ct);
        return [.. byParty.Select(pair => (pair.Key, PartyAccountRules.Summarize(kind, pair.Value, asOf)))];
    }
}
