using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Sales;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// A money account of the company. <see cref="Kind"/> is technical: what the POS posts to (Cash = the drawer, Card, Qr,
/// and the branch Safe) or Bank/Other for the ones the administration creates. <see cref="AccountTypeName"/> is how the
/// business groups its money (its own catalog of account types). <see cref="IsAutomatic"/>: a branch account the POS
/// posts to (Cash, Card, Qr), never deactivated. Company-wide when <see cref="BranchId"/> is null.
/// </summary>
public sealed record TreasuryAccountRecord(
    Guid AccountId,
    Guid? BranchId,
    string? BranchName,
    string Kind,
    string Name,
    decimal Balance,
    decimal TodayIn,
    decimal TodayOut,
    string? Description = null,
    Guid? AccountTypeId = null,
    string? AccountTypeName = null,
    bool IsActive = true,
    bool IsAutomatic = false);

/// <summary>
/// One movement of a treasury account. Nothing is ever deleted or rewritten: a voided movement stays, marked
/// <see cref="Voided"/> with its reason, and stops counting; an edited one is voided and replaced (the replacement
/// carries <see cref="CorrectsMovementId"/>). What the administration may do with it: <see cref="CanVoid"/> and
/// <see cref="CanEdit"/> (everything but sales and customer payments) or only <see cref="CanReclassify"/> (move a sale or
/// payment to another account; the operation itself is voided at the POS so the stock and the customer's account follow).
/// </summary>
public sealed record TreasuryMovementRecord(
    Guid MovementId,
    Guid AccountId,
    string Kind,
    string Direction,
    decimal Amount,
    DateTimeOffset OccurredAtUtc,
    DateOnly BusinessDate,
    string Concept,
    string? DocumentReference,
    Guid? CustomerId,
    string? CustomerName,
    string? SourceType,
    Guid? ReversesMovementId,
    bool Reversed,
    Guid? TransferId = null,
    bool Voided = false,
    string? VoidReason = null,
    DateTimeOffset? VoidedAtUtc = null,
    Guid? CorrectsMovementId = null,
    bool CanVoid = false,
    bool CanEdit = false,
    bool CanReclassify = false,
    Guid? RecurrenceId = null);

/// <summary>A movement to append to a treasury account.</summary>
public sealed record NewTreasuryMovement(
    Guid OrganizationId,
    Guid BranchId,
    string PaymentMethod,
    string Kind,
    decimal Amount,
    DateTimeOffset OccurredAtUtc,
    DateOnly BusinessDate,
    string Concept,
    string? DocumentReference,
    Guid? CustomerId,
    string SourceType,
    Guid SourceId,
    Guid CreatedByUserId);

/// <summary>
/// The new values of an edited movement; null keeps the current one. <see cref="ToAccountId"/> is the destination of a
/// transfer (<see cref="AccountId"/> its origin).
/// </summary>
public sealed record TreasuryMovementEdit(
    Guid? AccountId, Guid? ToAccountId, decimal? Amount, DateOnly? Date, string? Concept, string? Reference, string Reason);

public enum TreasuryWriteOutcome
{
    Done,
    AccountNotFound,
    BranchNotFound,
    AccountTypeNotFound,

    /// <summary>The branch already has its safe (one per branch).</summary>
    SafeAlreadyExists,

    /// <summary>A transfer from an account to itself.</summary>
    SameAccount,

    /// <summary>A new movement on, or a movement moved to, an inactive account.</summary>
    AccountInactive,

    /// <summary>The drawer, card or QR account of a branch: the POS posts to it, it stays active.</summary>
    AutomaticAccount,

    /// <summary>An account with money is not deactivated: transfer the balance first.</summary>
    BalanceNotZero,
    MovementNotFound,

    /// <summary>Already voided, a reversal, already reversed, or a sale/payment (void it at the POS).</summary>
    NotVoidable,

    /// <summary>A sale or payment: only its account can change.</summary>
    OnlyAccountEditable,
    NothingChanged,
}

/// <summary>
/// The company's treasury (0046-0048): the money accounts and their movements. Operation writers run INSIDE the caller's
/// transaction (a POS sale projection, a delivery settlement, the drawer of a cash session), so the money moves together
/// with the stock and the customer's account, or not at all; each posting names its source and one source posts once.
/// The administration manages the accounts (create, rename, re-type, deactivate) and the movements: records money in or
/// out by hand, transfers, and voids or edits a movement. A voided movement is kept and audited but no balance counts it;
/// an edit is a void plus its replacement. Every write leaves an audit row.
/// </summary>
public sealed class PostgresTreasuryStore
{
    public const string Cash = "Cash";
    public const string Card = "Card";
    public const string Qr = "Qr";
    public const string Safe = "Safe";
    public const string Bank = "Bank";
    public const string Other = "Other";

    public const string ManualIn = "ManualIn";
    public const string ManualOut = "ManualOut";
    public const string Transfer = "Transfer";

    /// <summary>The technical kinds of account the administration creates.</summary>
    public static readonly IReadOnlySet<string> ManualAccountKinds = new HashSet<string> { Safe, Bank, Other };

    /// <summary>Kinds the administration may void and fully edit.</summary>
    private static readonly HashSet<string> EditableKinds = ["CashCountDifference", "CashWithdrawal", "CashDeposit", Transfer, ManualIn, ManualOut];

    /// <summary>Kinds tied to a sale or a customer payment: only their account may change.</summary>
    private static readonly HashSet<string> ReclassifiableKinds = ["Sale", "CustomerPayment", "DeliveryPayment"];

    /// <summary>The default account types of an organization: (name, key, sort order).</summary>
    private static readonly (string Name, string Key, int Sort)[] DefaultTypes =
    [
        ("Efectivo", "efectivo", 10),
        ("Tarjetas", "tarjetas", 20),
        ("Billeteras virtuales / QR", "billeteras", 30),
        ("Bancos", "bancos", 40),
        ("Otras", "otras", 50),
    ];

    private const string KindOrder =
        "CASE a.kind WHEN 'Cash' THEN 0 WHEN 'Card' THEN 1 WHEN 'Qr' THEN 2 WHEN 'Safe' THEN 3 WHEN 'Bank' THEN 4 ELSE 5 END";

    /// <summary>The join condition that leaves voided movements out of every sum.</summary>
    private const string NotVoided =
        "NOT EXISTS (SELECT 1 FROM treasury_movement_voids v WHERE v.organization_id = m.organization_id AND v.movement_id = m.id)";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTreasuryStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>The treasury account kind of a POS tender method; null for a sale on current account (no money moves).</summary>
    public static string? KindOf(string? tenderMethod) => tenderMethod switch
    {
        SaleTender.Cash => Cash,
        SaleTender.Card => Card,
        SaleTender.Qr => Qr,
        _ => null,
    };

    private static string DefaultName(string kind) => kind switch
    {
        Cash => "Caja efectivo · ",
        Card => "Tarjetas · ",
        Safe => "Caja fuerte · ",
        _ => "QR / billeteras · ",
    };

    /// <summary>The key of the default account type of a technical kind.</summary>
    private static string DefaultTypeKey(string kind) => kind switch
    {
        Cash or Safe => "efectivo",
        Card => "tarjetas",
        Qr => "billeteras",
        Bank => "bancos",
        _ => "otras",
    };

    /// <summary>
    /// Seeds the default account types of the organization when it has none yet (an organization that already set up
    /// its own catalog is left alone).
    /// </summary>
    internal static async Task EnsureDefaultTypesAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, CancellationToken ct)
    {
        await using (var any = new NpgsqlCommand("SELECT 1 FROM treasury_account_types LIMIT 1", connection, tx))
        {
            if (await any.ExecuteScalarAsync(ct) is not null)
            {
                return;
            }
        }

        foreach (var (name, key, sort) in DefaultTypes)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO treasury_account_types (id, organization_id, name, key, sort_order) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING",
                connection, tx);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(organizationId);
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(sort);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<Guid?> TypeByKeyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id FROM treasury_account_types WHERE key = $1", connection, tx);
        cmd.Parameters.AddWithValue(key);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>The automatic account of a branch for a kind (Cash, Card, Qr, Safe), created when it does not exist yet.</summary>
    internal static async Task<Guid> EnsureAccountAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, string kind, CancellationToken ct)
    {
        await using (var find = new NpgsqlCommand("SELECT id FROM treasury_accounts WHERE branch_id = $1 AND kind = $2", connection, tx))
        {
            find.Parameters.AddWithValue(branchId);
            find.Parameters.AddWithValue(kind);
            if (await find.ExecuteScalarAsync(ct) is Guid existing)
            {
                return existing;
            }
        }

        await EnsureDefaultTypesAsync(connection, tx, organizationId, ct);
        var typeId = await TypeByKeyAsync(connection, tx, DefaultTypeKey(kind), ct);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO treasury_accounts (organization_id, id, branch_id, kind, name, account_type_id)
            SELECT $1, $2, $3, $4, $5 || name, $6 FROM branches WHERE id = $3
            ON CONFLICT (organization_id, branch_id, kind) WHERE kind IN ('Cash', 'Card', 'Qr', 'Safe') DO NOTHING
            """, connection, tx);
        insert.Parameters.AddWithValue(organizationId);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(branchId);
        insert.Parameters.AddWithValue(kind);
        insert.Parameters.AddWithValue(DefaultName(kind));
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)typeId ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        await using var again = new NpgsqlCommand("SELECT id FROM treasury_accounts WHERE branch_id = $1 AND kind = $2", connection, tx);
        again.Parameters.AddWithValue(branchId);
        again.Parameters.AddWithValue(kind);
        return (Guid)(await again.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Appends one movement. With a source, a second posting of the same source writes nothing (false). The id is the
    /// caller's so a transfer's legs, a correction and an audit row can name it.
    /// </summary>
    internal static async Task<bool> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid movementId, Guid accountId, string kind,
        string direction, decimal amount, DateTimeOffset occurredAtUtc, DateOnly businessDate, string concept, string? documentReference,
        Guid? customerId, string? sourceType, Guid? sourceId, Guid? transferId, Guid actorId, CancellationToken ct,
        Guid? correctsMovementId = null, Guid? recurrenceId = null)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO treasury_movements
                (organization_id, id, account_id, kind, direction, amount, occurred_at_utc, business_date, concept,
                 document_reference, customer_id, source_type, source_id, transfer_id, created_by_user_id, corrects_movement_id,
                 recurrence_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            ON CONFLICT (organization_id, source_type, source_id) WHERE source_type IS NOT NULL DO NOTHING
            """, connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(movementId);
        cmd.Parameters.AddWithValue(accountId);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(direction);
        cmd.Parameters.AddWithValue(amount);
        cmd.Parameters.AddWithValue(occurredAtUtc);
        cmd.Parameters.AddWithValue(businessDate);
        cmd.Parameters.AddWithValue(concept);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)documentReference ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)customerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)sourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)sourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)transferId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(actorId);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)correctsMovementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)recurrenceId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Puts money In the branch account of the movement's payment method. False (nothing written) when the payment method
    /// moves no money (current account) or the source was already posted.
    /// </summary>
    internal static async Task<bool> RecordInAsync(NpgsqlConnection connection, NpgsqlTransaction tx, NewTreasuryMovement movement, CancellationToken ct)
    {
        if (KindOf(movement.PaymentMethod) is not { } kind || movement.Amount <= 0m)
        {
            return false;
        }

        var accountId = await EnsureAccountAsync(connection, tx, movement.OrganizationId, movement.BranchId, kind, ct);
        return await InsertAsync(
            connection, tx, movement.OrganizationId, Guid.NewGuid(), accountId, movement.Kind, "In", movement.Amount, movement.OccurredAtUtc,
            movement.BusinessDate, movement.Concept, movement.DocumentReference, movement.CustomerId, movement.SourceType, movement.SourceId,
            null, movement.CreatedByUserId, ct);
    }

    /// <summary>
    /// Takes back what a source posted: one Reversal, in the opposite direction, of the movement that counts for it now
    /// (the original, or the replacement it was edited into; nothing when it was voided outright). The reversal's source
    /// is <paramref name="reversalSourceType"/> with the same id, so it is written once.
    /// </summary>
    internal static async Task<bool> ReverseSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string sourceType, Guid sourceId,
        string reversalSourceType, string concept, DateTimeOffset occurredAtUtc, DateOnly businessDate, Guid actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            WITH RECURSIVE chain AS (
                SELECT m.id AS current, 0 AS depth
                FROM treasury_movements m
                WHERE m.source_type = $1 AND m.source_id = $2 AND m.kind <> 'Reversal'
                UNION ALL
                SELECT v.replacement_movement_id, c.depth + 1
                FROM chain c JOIN treasury_movement_voids v ON v.movement_id = c.current
                WHERE v.replacement_movement_id IS NOT NULL AND c.depth < 100
            )
            INSERT INTO treasury_movements
                (organization_id, id, account_id, kind, direction, amount, occurred_at_utc, business_date, concept,
                 document_reference, customer_id, source_type, source_id, reverses_movement_id, created_by_user_id)
            SELECT e.organization_id, $3, e.account_id, 'Reversal', CASE e.direction WHEN 'In' THEN 'Out' ELSE 'In' END, e.amount, $4, $5, $6,
                   e.document_reference, e.customer_id, $7, $2, e.id, $8
            FROM chain c JOIN treasury_movements e ON e.id = c.current
            WHERE NOT EXISTS (SELECT 1 FROM treasury_movement_voids v WHERE v.movement_id = e.id)
            ON CONFLICT DO NOTHING
            """, connection, tx);
        cmd.Parameters.AddWithValue(sourceType);
        cmd.Parameters.AddWithValue(sourceId);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(occurredAtUtc);
        cmd.Parameters.AddWithValue(businessDate);
        cmd.Parameters.AddWithValue(concept);
        cmd.Parameters.AddWithValue(reversalSourceType);
        cmd.Parameters.AddWithValue(actorId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // ---- reads ------------------------------------------------------------------------------------

    /// <summary>Every treasury account of the organization (or of one branch) with its balance and today's In/Out.</summary>
    public async Task<IReadOnlyList<TreasuryAccountRecord>> ListAccountsAsync(CloudTenantScope scope, Guid? branchId, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        // The catalog of account types exists as soon as the treasury is looked at.
        await EnsureDefaultTypesAsync(connection, tx, scope.OrganizationId, ct);
        // The recurring movements due by today are recorded before the balances are read.
        await PostgresTreasuryRecurrenceStore.GenerateDueAsync(connection, tx, scope.OrganizationId, today, ct);
        var accounts = await ReadAccountsAsync(connection, tx, "($1::uuid IS NULL OR a.branch_id = $1)", branchId, today, ct);
        await tx.CommitAsync(ct);
        return accounts;
    }

    private static async Task<List<TreasuryAccountRecord>> ReadAccountsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string where, Guid? parameter, DateOnly today, CancellationToken ct)
    {
        var accounts = new List<TreasuryAccountRecord>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT a.id, a.branch_id, b.name, a.kind, a.name, a.description, a.account_type_id, t.name, a.is_active,
                   COALESCE(SUM(CASE WHEN m.direction = 'In' THEN m.amount ELSE -m.amount END), 0),
                   COALESCE(SUM(CASE WHEN m.direction = 'In' AND m.business_date = $2 THEN m.amount END), 0),
                   COALESCE(SUM(CASE WHEN m.direction = 'Out' AND m.business_date = $2 THEN m.amount END), 0)
            FROM treasury_accounts a
            LEFT JOIN branches b ON b.id = a.branch_id
            LEFT JOIN treasury_account_types t ON t.organization_id = a.organization_id AND t.id = a.account_type_id
            LEFT JOIN treasury_movements m ON m.organization_id = a.organization_id AND m.account_id = a.id AND {NotVoided}
            WHERE {where}
            GROUP BY a.id, a.branch_id, b.name, a.kind, a.name, a.description, a.account_type_id, t.name, a.is_active, t.sort_order
            ORDER BY t.sort_order NULLS LAST, b.name NULLS FIRST, {KindOrder}, a.name
            """, connection, tx);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)parameter ?? DBNull.Value);
        cmd.Parameters.AddWithValue(today);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(3);
            accounts.Add(new TreasuryAccountRecord(
                reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                kind, reader.GetString(4), reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8), IsAutomaticKind(kind)));
        }

        return accounts;
    }

    private static bool IsAutomaticKind(string kind) => kind is Cash or Card or Qr;

    /// <summary>The movements of one account in [from, to] (business dates), newest first; null when the account is not the organization's.</summary>
    public async Task<IReadOnlyList<TreasuryMovementRecord>?> ListMovementsAsync(
        CloudTenantScope scope, Guid accountId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        if (await AccountAsync(connection, tx, accountId, ct) is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var movements = await ReadMovementsAsync(
            connection, tx,
            "m.account_id = $1 AND ($2::date IS NULL OR m.business_date >= $2) AND ($3::date IS NULL OR m.business_date <= $3)",
            ct, accountId, (NpgsqlTypes.NpgsqlDbType.Date, from), (NpgsqlTypes.NpgsqlDbType.Date, to));
        await tx.CommitAsync(ct);
        return movements;
    }

    private static async Task<List<TreasuryMovementRecord>> ReadMovementsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string where, CancellationToken ct, Guid first,
        params (NpgsqlTypes.NpgsqlDbType Type, object? Value)[] more)
    {
        var movements = new List<TreasuryMovementRecord>();
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT m.id, m.account_id, m.kind, m.direction, m.amount, m.occurred_at_utc, m.business_date, m.concept,
                   m.document_reference, m.customer_id, c.display_name, m.source_type, m.reverses_movement_id,
                   EXISTS (SELECT 1 FROM treasury_movements r
                           WHERE r.organization_id = m.organization_id AND r.reverses_movement_id = m.id),
                   m.transfer_id, v.movement_id IS NOT NULL, v.reason, v.voided_at_utc, m.corrects_movement_id, m.recurrence_id
            FROM treasury_movements m
            LEFT JOIN customers c ON c.organization_id = m.organization_id AND c.id = m.customer_id
            LEFT JOIN treasury_movement_voids v ON v.organization_id = m.organization_id AND v.movement_id = m.id
            WHERE {where}
            ORDER BY m.occurred_at_utc DESC, m.created_at DESC
            LIMIT 1000
            """, connection, tx);
        cmd.Parameters.AddWithValue(first);
        foreach (var (type, value) in more)
        {
            cmd.Parameters.AddWithValue(type, value ?? DBNull.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(2);
            var reversed = reader.GetBoolean(13);
            var voided = reader.GetBoolean(15);
            var open = !reversed && !voided && kind != "Reversal";
            movements.Add(new TreasuryMovementRecord(
                reader.GetGuid(0), reader.GetGuid(1), kind, reader.GetString(3), reader.GetDecimal(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateOnly>(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12), reversed,
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                voided,
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                reader.IsDBNull(18) ? null : reader.GetGuid(18),
                CanVoid: open && EditableKinds.Contains(kind),
                CanEdit: open && EditableKinds.Contains(kind),
                CanReclassify: open && ReclassifiableKinds.Contains(kind),
                RecurrenceId: reader.IsDBNull(19) ? null : reader.GetGuid(19)));
        }

        return movements;
    }

    private sealed record AccountRow(Guid Id, string Name, string Kind, bool IsActive);

    private static async Task<AccountRow?> AccountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid accountId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, name, kind, is_active FROM treasury_accounts WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new AccountRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)) : null;
    }

    internal static async Task<string?> NameOfAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid accountId, CancellationToken ct) =>
        (await AccountAsync(connection, tx, accountId, ct))?.Name;

    private static async Task<bool> ActiveTypeExistsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid typeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT is_active FROM treasury_account_types WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(typeId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    // ---- accounts ---------------------------------------------------------------------------------

    /// <summary>
    /// Creates an account of an account type (its default type when none is given). A safe belongs to a branch (one per
    /// branch, where the POS sends what it withdraws "to the safe"); any other account is company-wide unless a branch is
    /// given.
    /// </summary>
    public async Task<(TreasuryWriteOutcome Outcome, TreasuryAccountRecord? Account)> CreateAccountAsync(
        CloudTenantScope scope, Guid actorId, string kind, string name, Guid? accountTypeId, Guid? branchId, string? description, DateOnly today,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await EnsureDefaultTypesAsync(connection, tx, scope.OrganizationId, ct);

        if (branchId is { } branch)
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM branches WHERE id = $1", connection, tx);
            exists.Parameters.AddWithValue(branch);
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                await tx.RollbackAsync(ct);
                return (TreasuryWriteOutcome.BranchNotFound, null);
            }
        }

        if (kind == Safe)
        {
            await using var existing = new NpgsqlCommand("SELECT 1 FROM treasury_accounts WHERE branch_id = $1 AND kind = 'Safe'", connection, tx);
            existing.Parameters.AddWithValue(branchId!.Value);
            if (await existing.ExecuteScalarAsync(ct) is not null)
            {
                await tx.RollbackAsync(ct);
                return (TreasuryWriteOutcome.SafeAlreadyExists, null);
            }
        }

        var typeId = accountTypeId ?? await TypeByKeyAsync(connection, tx, DefaultTypeKey(kind), ct);
        if (accountTypeId is { } chosen && !await ActiveTypeExistsAsync(connection, tx, chosen, ct))
        {
            await tx.RollbackAsync(ct);
            return (TreasuryWriteOutcome.AccountTypeNotFound, null);
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO treasury_accounts (organization_id, id, branch_id, kind, name, description, account_type_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(scope.OrganizationId);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)branchId ?? DBNull.Value);
            insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)description ?? DBNull.Value);
            insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)typeId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", id, "treasury.account_created",
            new { kind, name, branchId, accountTypeId = typeId, description }, ct);
        var account = (await ReadAccountsAsync(connection, tx, "a.id = $1", id, today, ct)).Single();
        await tx.CommitAsync(ct);
        return (TreasuryWriteOutcome.Done, account);
    }

    /// <summary>Renames, re-types or re-describes an account (its branch and technical kind never change).</summary>
    public async Task<(TreasuryWriteOutcome Outcome, TreasuryAccountRecord? Account)> UpdateAccountAsync(
        CloudTenantScope scope, Guid actorId, Guid accountId, string name, Guid? accountTypeId, string? description, DateOnly today,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var before = (await ReadAccountsAsync(connection, tx, "a.id = $1", accountId, today, ct)).SingleOrDefault();
        if (before is null)
        {
            await tx.RollbackAsync(ct);
            return (TreasuryWriteOutcome.AccountNotFound, null);
        }

        // A type already assigned may stay even if it was deactivated since; a new one must be active.
        if (accountTypeId is { } chosen && chosen != before.AccountTypeId && !await ActiveTypeExistsAsync(connection, tx, chosen, ct))
        {
            await tx.RollbackAsync(ct);
            return (TreasuryWriteOutcome.AccountTypeNotFound, null);
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE treasury_accounts SET name = $2, account_type_id = $3, description = $4, updated_at = now() WHERE id = $1",
            connection, tx))
        {
            update.Parameters.AddWithValue(accountId);
            update.Parameters.AddWithValue(name);
            update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)accountTypeId ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)description ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
        }

        var after = (await ReadAccountsAsync(connection, tx, "a.id = $1", accountId, today, ct)).Single();
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", accountId, "treasury.account_updated",
            new { name = after.Name, accountTypeId = after.AccountTypeId, description = after.Description }, ct,
            old: new { name = before.Name, accountTypeId = before.AccountTypeId, description = before.Description });
        await tx.CommitAsync(ct);
        return (TreasuryWriteOutcome.Done, after);
    }

    /// <summary>
    /// Activates or deactivates an account. The drawer, card and QR accounts of a branch stay active (the POS posts to
    /// them), and an account with money is not deactivated (transfer its balance first).
    /// </summary>
    public async Task<TreasuryWriteOutcome> SetAccountActiveAsync(
        CloudTenantScope scope, Guid actorId, Guid accountId, bool isActive, DateOnly today, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var account = (await ReadAccountsAsync(connection, tx, "a.id = $1", accountId, today, ct)).SingleOrDefault();
        var refusal = account switch
        {
            null => TreasuryWriteOutcome.AccountNotFound,
            { IsActive: var current } when current == isActive => TreasuryWriteOutcome.NothingChanged,
            { IsAutomatic: true } when !isActive => TreasuryWriteOutcome.AutomaticAccount,
            { Balance: not 0m } when !isActive => TreasuryWriteOutcome.BalanceNotZero,
            _ => TreasuryWriteOutcome.Done,
        };
        if (refusal != TreasuryWriteOutcome.Done)
        {
            await tx.RollbackAsync(ct);
            return refusal;
        }

        await using (var update = new NpgsqlCommand("UPDATE treasury_accounts SET is_active = $2, updated_at = now() WHERE id = $1", connection, tx))
        {
            update.Parameters.AddWithValue(accountId);
            update.Parameters.AddWithValue(isActive);
            await update.ExecuteNonQueryAsync(ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", accountId,
            isActive ? "treasury.account_activated" : "treasury.account_deactivated", new { isActive }, ct);
        await tx.CommitAsync(ct);
        return TreasuryWriteOutcome.Done;
    }

    // ---- movements by hand --------------------------------------------------------------------------

    /// <summary>Money In or Out of an active account recorded by hand (a deposit slip, an expense paid by the bank, the change fund...).</summary>
    public async Task<(TreasuryWriteOutcome Outcome, Guid? MovementId)> RecordManualAsync(
        CloudTenantScope scope, Guid actorId, Guid accountId, string direction, decimal amount, DateOnly date, string concept,
        string? reference, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var account = await AccountAsync(connection, tx, accountId, ct);
        if (account is not { IsActive: true })
        {
            await tx.RollbackAsync(ct);
            return (account is null ? TreasuryWriteOutcome.AccountNotFound : TreasuryWriteOutcome.AccountInactive, null);
        }

        var id = Guid.NewGuid();
        await InsertAsync(connection, tx, scope.OrganizationId, id, accountId, direction == "In" ? ManualIn : ManualOut, direction, amount,
            DateTimeOffset.UtcNow, date, concept, reference, null, null, null, null, actorId, ct);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", accountId, "treasury.movement_recorded",
            new { movementId = id, direction, amount, date, concept, reference }, ct);
        await tx.CommitAsync(ct);
        return (TreasuryWriteOutcome.Done, id);
    }

    /// <summary>Money moved from one active account to another: an Out and an In sharing a transfer id, one audit row.</summary>
    public async Task<(TreasuryWriteOutcome Outcome, Guid? TransferId)> TransferAsync(
        CloudTenantScope scope, Guid actorId, Guid fromAccountId, Guid toAccountId, decimal amount, DateOnly date, string concept,
        string? reference, CancellationToken ct)
    {
        if (fromAccountId == toAccountId)
        {
            return (TreasuryWriteOutcome.SameAccount, null);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        var from = await AccountAsync(connection, tx, fromAccountId, ct);
        var to = await AccountAsync(connection, tx, toAccountId, ct);
        if (from is null || to is null || !from.IsActive || !to.IsActive)
        {
            await tx.RollbackAsync(ct);
            return (from is null || to is null ? TreasuryWriteOutcome.AccountNotFound : TreasuryWriteOutcome.AccountInactive, null);
        }

        var transferId = Guid.NewGuid();
        await WriteTransferAsync(connection, tx, scope.OrganizationId, transferId, fromAccountId, from.Name, toAccountId, to.Name, amount,
            DateTimeOffset.UtcNow, date, concept, reference, null, actorId, ct);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", fromAccountId, "treasury.transfer_recorded",
            new { transferId, fromAccountId, toAccountId, amount, date, concept, reference }, ct);
        await tx.CommitAsync(ct);
        return (TreasuryWriteOutcome.Done, transferId);
    }

    /// <summary>
    /// Writes both legs of a transfer: "Transferencia a {to}" Out of <paramref name="fromAccountId"/> and "Transferencia
    /// desde {from}" In <paramref name="toAccountId"/>. With a source, each leg is keyed by it (Out: the source type, In:
    /// the source type + "In"), so a retried projection writes nothing twice. Returns the (Out, In) movement ids.
    /// </summary>
    internal static async Task<(Guid Out, Guid In)> WriteTransferAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid transferId, Guid fromAccountId, string fromName,
        Guid toAccountId, string toName, decimal amount, DateTimeOffset occurredAtUtc, DateOnly date, string concept, string? reference,
        (string Type, Guid Id)? source, Guid actorId, CancellationToken ct, (Guid Out, Guid In)? corrects = null)
    {
        var (outId, inId) = (Guid.NewGuid(), Guid.NewGuid());
        await InsertAsync(connection, tx, organizationId, outId, fromAccountId, Transfer, "Out", amount, occurredAtUtc, date,
            $"{TransferToPrefix}{toName}: {concept}", reference, null, source?.Type, source?.Id, transferId, actorId, ct, corrects?.Out);
        await InsertAsync(connection, tx, organizationId, inId, toAccountId, Transfer, "In", amount, occurredAtUtc, date,
            $"{TransferFromPrefix}{fromName}: {concept}", reference, null, source is { } s ? s.Type + "In" : null, source?.Id, transferId,
            actorId, ct, corrects?.In);
        return (outId, inId);
    }

    private const string TransferToPrefix = "Transferencia a ";
    private const string TransferFromPrefix = "Transferencia desde ";

    /// <summary>The concept a transfer was given, without the "Transferencia a X: " its legs add.</summary>
    private static string BaseConcept(TreasuryMovementRecord leg) =>
        leg.Kind == Transfer && (leg.Concept.StartsWith(TransferToPrefix, StringComparison.Ordinal) || leg.Concept.StartsWith(TransferFromPrefix, StringComparison.Ordinal))
        && leg.Concept.IndexOf(": ", StringComparison.Ordinal) is var at and > 0
            ? leg.Concept[(at + 2)..]
            : leg.Concept;

    // ---- void and edit ----------------------------------------------------------------------------

    private static async Task<TreasuryMovementRecord?> MovementAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid movementId, CancellationToken ct) =>
        (await ReadMovementsAsync(connection, tx, "m.id = $1", ct, movementId)).SingleOrDefault();

    /// <summary>Both legs of a transfer (Out first), or the movement alone.</summary>
    private static async Task<IReadOnlyList<TreasuryMovementRecord>> LegsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, TreasuryMovementRecord movement, CancellationToken ct) =>
        movement is { Kind: Transfer, TransferId: { } transfer }
            ? (await ReadMovementsAsync(connection, tx, "m.transfer_id = $1 AND m.kind = 'Transfer'", ct, transfer))
                .Where(leg => !leg.Voided).OrderBy(leg => leg.Direction == "Out" ? 0 : 1).ToList()
            : [movement];

    private static async Task InsertVoidAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid movementId, Guid actorId, string reason, Guid? replacement,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO treasury_movement_voids (organization_id, movement_id, voided_by_user_id, reason, replacement_movement_id)
            VALUES ($1, $2, $3, $4, $5)
            """, connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(movementId);
        cmd.Parameters.AddWithValue(actorId);
        cmd.Parameters.AddWithValue(reason);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)replacement ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object Snapshot(TreasuryMovementRecord movement) => new
    {
        movementId = movement.MovementId,
        accountId = movement.AccountId,
        kind = movement.Kind,
        direction = movement.Direction,
        amount = movement.Amount,
        date = movement.BusinessDate,
        concept = movement.Concept,
        reference = movement.DocumentReference,
        transferId = movement.TransferId,
    };

    /// <summary>
    /// Voids a movement (both legs of a transfer): it stays, marked voided with the reason, and no balance counts it any
    /// more. Sales and customer payments are voided at the POS instead (so the stock and the customer's account follow).
    /// </summary>
    public async Task<TreasuryWriteOutcome> VoidAsync(CloudTenantScope scope, Guid actorId, Guid movementId, string reason, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var target = await MovementAsync(connection, tx, movementId, ct);
        if (target is null || !target.CanVoid)
        {
            await tx.RollbackAsync(ct);
            return target is null ? TreasuryWriteOutcome.MovementNotFound : TreasuryWriteOutcome.NotVoidable;
        }

        var legs = await LegsAsync(connection, tx, target, ct);
        foreach (var leg in legs)
        {
            await InsertVoidAsync(connection, tx, scope.OrganizationId, leg.MovementId, actorId, reason, null, ct);
        }

        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", target.AccountId, "treasury.movement_voided",
            new { reason, voided = legs.Select(leg => leg.MovementId) }, ct, old: legs.Select(Snapshot));
        await tx.CommitAsync(ct);
        return TreasuryWriteOutcome.Done;
    }

    /// <summary>
    /// Edits a movement: the original is voided (pointing at its replacement) and the replacement, with the new values,
    /// carries <see cref="TreasuryMovementRecord.CorrectsMovementId"/>; a transfer is edited as a whole (both legs). A sale or
    /// customer payment only changes account ("it was paid by card, not cash"). Balances follow at once.
    /// </summary>
    public async Task<(TreasuryWriteOutcome Outcome, Guid? ReplacementId)> EditAsync(
        CloudTenantScope scope, Guid actorId, Guid movementId, TreasuryMovementEdit edit, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        async Task<(TreasuryWriteOutcome, Guid?)> Refuse(TreasuryWriteOutcome outcome)
        {
            await tx.RollbackAsync(ct);
            return (outcome, null);
        }

        var target = await MovementAsync(connection, tx, movementId, ct);
        if (target is null)
        {
            return await Refuse(TreasuryWriteOutcome.MovementNotFound);
        }

        if (!target.CanEdit && !target.CanReclassify)
        {
            return await Refuse(TreasuryWriteOutcome.NotVoidable);
        }

        if (target.CanReclassify
            && (edit.Amount is { } a && a != target.Amount || edit.Date is { } d && d != target.BusinessDate
                || edit.Concept is { } c && c != target.Concept || edit.ToAccountId is not null))
        {
            return await Refuse(TreasuryWriteOutcome.OnlyAccountEditable);
        }

        var legs = await LegsAsync(connection, tx, target, ct);
        var isTransfer = target.Kind == Transfer && legs.Count == 2;
        var outLeg = legs[0];
        var inLeg = isTransfer ? legs[1] : null;

        var accountId = edit.AccountId ?? outLeg.AccountId;
        var toAccountId = isTransfer ? edit.ToAccountId ?? inLeg!.AccountId : (Guid?)null;
        var amount = edit.Amount ?? outLeg.Amount;
        var date = edit.Date ?? outLeg.BusinessDate;
        var concept = edit.Concept ?? BaseConcept(outLeg);
        var reference = edit.Reference is null ? outLeg.DocumentReference : edit.Reference.Length == 0 ? null : edit.Reference;
        if (accountId == outLeg.AccountId && toAccountId == inLeg?.AccountId && amount == outLeg.Amount && date == outLeg.BusinessDate
            && concept == BaseConcept(outLeg) && reference == outLeg.DocumentReference)
        {
            return await Refuse(TreasuryWriteOutcome.NothingChanged);
        }

        if (isTransfer && accountId == toAccountId)
        {
            return await Refuse(TreasuryWriteOutcome.SameAccount);
        }

        // A movement moved to another account needs that account to exist and be active.
        foreach (var (moved, original) in new[] { (accountId, outLeg.AccountId), (toAccountId ?? Guid.Empty, inLeg?.AccountId ?? Guid.Empty) })
        {
            if (moved != original && await AccountAsync(connection, tx, moved, ct) is var account && account is not { IsActive: true })
            {
                return await Refuse(account is null ? TreasuryWriteOutcome.AccountNotFound : TreasuryWriteOutcome.AccountInactive);
            }
        }

        Guid replacementId;
        if (isTransfer)
        {
            var fromName = (await AccountAsync(connection, tx, accountId, ct))!.Name;
            var toName = (await AccountAsync(connection, tx, toAccountId!.Value, ct))!.Name;
            var (newOut, newIn) = await WriteTransferAsync(connection, tx, scope.OrganizationId, Guid.NewGuid(), accountId, fromName,
                toAccountId.Value, toName, amount, outLeg.OccurredAtUtc, date, concept, reference, null, actorId, ct,
                (outLeg.MovementId, inLeg!.MovementId));
            await InsertVoidAsync(connection, tx, scope.OrganizationId, outLeg.MovementId, actorId, $"Editado: {edit.Reason}", newOut, ct);
            await InsertVoidAsync(connection, tx, scope.OrganizationId, inLeg.MovementId, actorId, $"Editado: {edit.Reason}", newIn, ct);
            replacementId = target.Direction == "Out" ? newOut : newIn;
        }
        else
        {
            replacementId = Guid.NewGuid();
            await InsertAsync(connection, tx, scope.OrganizationId, replacementId, accountId, target.Kind, target.Direction, amount,
                target.OccurredAtUtc, date, concept, reference, target.CustomerId, null, null, null, actorId, ct, target.MovementId);
            await InsertVoidAsync(connection, tx, scope.OrganizationId, target.MovementId, actorId, $"Editado: {edit.Reason}", replacementId, ct);
        }

        var replaced = (await LegsAsync(connection, tx, (await MovementAsync(connection, tx, replacementId, ct))!, ct)).Select(Snapshot);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury-account", accountId, "treasury.movement_edited",
            new { reason = edit.Reason, movements = replaced }, ct, old: legs.Select(Snapshot));
        await tx.CommitAsync(ct);
        return (TreasuryWriteOutcome.Done, replacementId);
    }

    // ---- reprocess ---------------------------------------------------------------------------------

    /// <summary>The operations whose money the treasury follows, as the terminals pushed them.</summary>
    private static readonly string[] MoneyPayloadKinds =
    [
        Commerce.Domain.Sync.Payloads.SalePayloadKinds.Sale,
        Commerce.Domain.Sync.Payloads.SalePayloadKinds.Voided,
        Commerce.Domain.Sync.Payloads.CustomerPaymentPayloadKinds.Received,
        Commerce.Domain.Sync.Payloads.CustomerPaymentPayloadKinds.Voided,
        Commerce.Domain.Sync.Payloads.CashMovementPayloadKinds.Recorded,
        Commerce.Domain.Sync.Payloads.CashSessionPayloadKinds.Closed,
    ];

    /// <summary>
    /// Brings into the treasury every operation the terminals already pushed (sales, their voids, customer payments, cash
    /// movements, cash count differences) that it does not hold yet: what was received before the treasury existed, or
    /// whose posting failed. It replays the same money projections ingestion runs, in the order things happened; each one
    /// is keyed by its source, so what is already there is never posted twice. Returns how many movements it added.
    /// </summary>
    public async Task<int> ReprocessAsync(CloudTenantScope scope, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);

        var before = await CountMovementsAsync(connection, tx, ct);
        var envelopes = new List<Commerce.Domain.Sync.SyncEnvelope>();
        await using (var read = new NpgsqlCommand(
            """
            SELECT operation_id, organization_id, branch_id, aggregate_id, aggregate_version, actor_id, correlation_id,
                   occurred_at_utc, payload_kind, payload::text
            FROM sync_inbox
            WHERE payload_kind = ANY($1)
            ORDER BY occurred_at_utc, operation_id
            """, connection, tx))
        {
            read.Parameters.AddWithValue(MoneyPayloadKinds);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                envelopes.Add(new Commerce.Domain.Sync.SyncEnvelope(
                    reader.GetGuid(0), 1, reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetInt64(4), reader.GetGuid(5),
                    reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9)));
            }
        }

        foreach (var envelope in envelopes)
        {
            if (envelope.PayloadKind == Commerce.Domain.Sync.Payloads.SalePayloadKinds.Voided)
            {
                await ReplaySaleVoidAsync(connection, tx, envelope, ct);
                continue;
            }

            // Each projection contains its own failure (savepoint): one bad envelope never blocks the others.
            await PosSaleAccountProjection.ProjectAsync(connection, tx, envelope, null, ct);
            await CustomerPaymentProjection.ProjectAsync(connection, tx, envelope, null, ct);
            await CashDrawerProjection.ProjectAsync(connection, tx, envelope, null, ct);
        }

        var added = (int)(await CountMovementsAsync(connection, tx, ct) - before);
        await AuditAsync(connection, tx, scope.OrganizationId, actorId, "treasury", scope.OrganizationId, "treasury.reprocessed",
            new { operations = envelopes.Count, movementsAdded = added }, ct);
        await tx.CommitAsync(ct);
        return added;
    }

    /// <summary>The money side only of a sale void (its stock was reversed when it was ingested).</summary>
    private static async Task ReplaySaleVoidAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Commerce.Domain.Sync.SyncEnvelope envelope, CancellationToken ct)
    {
        const string savepoint = "treasury_reprocess_void";
        await tx.SaveAsync(savepoint, ct);
        try
        {
            var payload = Commerce.Domain.Sync.SyncPayloadCodec.Deserialize<Commerce.Domain.Sync.Payloads.SaleVoidedPayloadV1>(envelope.Payload);
            if (payload.SaleId != Guid.Empty)
            {
                await PosSaleAccountProjection.ReverseAsync(
                    connection, tx, envelope.OrganizationId, payload.SaleId, payload.Reason, payload.VoidedAtUtc, payload.VoidedByOperatorId, ct);
            }

            await tx.ReleaseAsync(savepoint, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
        {
            await tx.RollbackAsync(savepoint, ct);
        }
    }

    private static async Task<long> CountMovementsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var count = new NpgsqlCommand("SELECT count(*) FROM treasury_movements", connection, tx);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    internal static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid actorId, string entityType, Guid entityId,
        string action, object detail, CancellationToken ct, string actorKind = "org-user", object? old = null) =>
        AuditLogWriter.InsertAsync(connection, tx, new UserManagementAuditEntry(
            actorKind, actorId, organizationId, entityType, entityId, action,
            old is null ? null : JsonSerializer.Serialize(old), JsonSerializer.Serialize(detail)), ct);

    /// <summary>The payment terms of a customer: its own days, else the organization's default (inside the caller's transaction).</summary>
    internal static async Task<PaymentTerms> CustomerTermsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid customerId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT c.payment_terms_days, o.default_customer_payment_terms_days
            FROM customers c JOIN organizations o ON o.id = c.organization_id
            WHERE c.id = $1
            """, connection, tx);
        cmd.Parameters.AddWithValue(customerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? PaymentTerms.For(reader.IsDBNull(0) ? null : reader.GetInt16(0), reader.GetInt16(1))
            : PaymentTerms.For(null, PaymentTerms.DefaultDays);
    }
}

/// <summary>The organization's catalog of treasury account types (`treasury_account_types`); audit entity "treasury_account_type".</summary>
public sealed class PostgresTreasuryAccountTypeStore : PostgresMasterDataStore
{
    public PostgresTreasuryAccountTypeStore(NpgsqlDataSource dataSource) : base(dataSource, "treasury_account_types", "treasury_account_type") { }
}
