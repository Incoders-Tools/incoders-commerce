using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store of the supplier current account (`current_account_movements`, append-only: the application role
/// can only SELECT and INSERT). Every method opens its own transaction with `set_config` first; the two writes
/// (register, reverse) add their audit row in the same transaction. A cross-organization supplier or movement is
/// invisible under RLS (null / NotFound). The balance is never stored: it is derived from the movements by the pure
/// rules in <see cref="CurrentAccountRules"/>.
/// </summary>
public sealed class PostgresCurrentAccountStore
{
    private const string Columns =
        "id, supplier_id, kind, direction, amount, occurred_on, due_on, document_reference, concept, reverses_movement_id, created_at_utc, created_by_user_id";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresCurrentAccountStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static AccountMovementRecord Read(NpgsqlDataReader r) => new(
        Id: r.GetGuid(0),
        SupplierId: r.GetGuid(1),
        Kind: Enum.Parse<AccountMovementKind>(r.GetString(2)),
        Direction: Enum.Parse<AccountDirection>(r.GetString(3)),
        Amount: r.GetDecimal(4),
        OccurredOn: r.GetFieldValue<DateOnly>(5),
        DueOn: r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6),
        DocumentReference: r.IsDBNull(7) ? null : r.GetString(7),
        Concept: r.GetString(8),
        ReversesMovementId: r.IsDBNull(9) ? null : r.GetGuid(9),
        CreatedAtUtc: r.GetFieldValue<DateTimeOffset>(10),
        CreatedByUserId: r.GetGuid(11));

    private static async Task<(bool Exists, int? PaymentTermsDays)> FindSupplierAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid supplierId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT payment_terms_days FROM suppliers WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(supplierId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (true, reader.IsDBNull(0) ? null : reader.GetInt32(0))
            : (false, null);
    }

    /// <summary>Appends a movement inside the caller's transaction (tenant scope already applied). Shared with the goods reception flow.</summary>
    internal static async Task<AccountMovementRecord> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid supplierId,
        NewAccountMovement movement, Guid? reversesMovementId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO current_account_movements
                (id, organization_id, party_kind, party_id, supplier_id, kind, direction, amount, occurred_on, due_on,
                 document_reference, concept, reverses_movement_id, created_by_user_id)
            VALUES ($1, $2, 'Supplier', $3, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            RETURNING {Columns}
            """, connection, tx);
        cmd.Parameters.AddWithValue(movement.Id);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(supplierId);
        cmd.Parameters.AddWithValue(movement.Kind.ToString());
        cmd.Parameters.AddWithValue(movement.Direction.ToString());
        cmd.Parameters.AddWithValue(movement.Amount);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, movement.OccurredOn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Date, (object?)movement.DueOn ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)movement.DocumentReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue(movement.Concept);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)reversesMovementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(movement.CreatedByUserId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    internal static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, string actorKind, Guid actorId,
        Guid supplierId, string action, AccountMovementRecord movement, CancellationToken ct) =>
        AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "supplier", supplierId, action, OldValueJson: null,
                NewValueJson:
                $$"""{"movementId":"{{movement.Id}}","kind":"{{movement.Kind}}","direction":"{{movement.Direction}}","amount":{{movement.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""),
            ct);

    /// <summary>Appends a movement. Null when the supplier is not visible in the scope's organization.</summary>
    public async Task<AccountMovementRecord?> RegisterAsync(
        CloudTenantScope scope, Guid supplierId, NewAccountMovement movement, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var (exists, terms) = await FindSupplierAsync(connection, tx, supplierId, ct);
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var due = movement.DueOn ?? CurrentAccountRules.DefaultDueOn(movement.Kind, movement.OccurredOn, terms);
        var record = await InsertAsync(connection, tx, scope.OrganizationId, supplierId, movement with { DueOn = due }, null, ct);
        await AuditAsync(connection, tx, scope, actorKind, actorId, supplierId, "supplier.movement_registered", record, ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// Reverses a movement of the supplier with a compensating Reversal (opposite direction, same amount, same
    /// document reference). A movement is reversed at most once (partial unique index) and a Reversal cannot be reversed.
    /// `OccurredOn` null takes today (Argentina time) or the original's date when that is later.
    /// </summary>
    public async Task<ReverseMovementResult> ReverseAsync(
        CloudTenantScope scope, Guid supplierId, Guid movementId, string? concept, DateOnly? occurredOn, DateOnly today,
        string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var result = await ReverseWithinAsync(
            connection, tx, scope.OrganizationId, supplierId, movementId, concept, occurredOn, today, actorId, ct);
        if (result.Outcome != ReverseMovementOutcome.Reversed)
        {
            await tx.RollbackAsync(ct);
            return result;
        }

        await AuditAsync(connection, tx, scope, actorKind, actorId, supplierId, "supplier.movement_reversed", result.Reversal!, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// The reversal itself, inside the CALLER's transaction (tenant scope already applied; the caller commits or rolls
    /// back and writes the audit row). Used directly by the goods reception void so the supplier Invoice and the stock
    /// come back in one transaction. On any outcome other than Reversed nothing was written, except that a unique
    /// violation (AlreadyReversed) leaves the transaction aborted: the caller must roll back.
    /// </summary>
    internal static async Task<ReverseMovementResult> ReverseWithinAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid supplierId, Guid movementId,
        string? concept, DateOnly? occurredOn, DateOnly today, Guid actorId, CancellationToken ct)
    {
        AccountMovementRecord? original = null;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE id = $1 AND supplier_id = $2", connection, tx))
        {
            cmd.Parameters.AddWithValue(movementId);
            cmd.Parameters.AddWithValue(supplierId);
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
            var record = await InsertAsync(connection, tx, organizationId, supplierId, reversal, original.Id, ct);
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
    /// Every movement of the supplier in ledger order (date, then registration), or null when the supplier is not
    /// visible. The statement and the summary are derived from this list by the caller.
    /// </summary>
    public async Task<IReadOnlyList<AccountMovementRecord>?> ListMovementsAsync(
        CloudTenantScope scope, Guid supplierId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var (exists, _) = await FindSupplierAsync(connection, tx, supplierId, ct);
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var results = new List<AccountMovementRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements WHERE supplier_id = $1 ORDER BY occurred_on, created_at_utc, id",
            connection, tx))
        {
            cmd.Parameters.AddWithValue(supplierId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>Balance and overdue, as of a date, of every supplier of the organization that has movements.</summary>
    public async Task<IReadOnlyList<SupplierAccountBalance>> BalancesAsync(CloudTenantScope scope, DateOnly asOf, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var bySupplier = new Dictionary<Guid, List<AccountMovementFact>>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM current_account_movements ORDER BY supplier_id, occurred_on, created_at_utc, id", connection, tx))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var movement = Read(reader);
                if (!bySupplier.TryGetValue(movement.SupplierId, out var list))
                {
                    bySupplier[movement.SupplierId] = list = [];
                }

                list.Add(movement.ToFact());
            }
        }

        await tx.CommitAsync(ct);
        return bySupplier
            .Select(pair =>
            {
                var summary = CurrentAccountRules.Summarize(pair.Value, asOf);
                return new SupplierAccountBalance(pair.Key, summary.Balance, summary.Overdue);
            })
            .ToList();
    }
}
