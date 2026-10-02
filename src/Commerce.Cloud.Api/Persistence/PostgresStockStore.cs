using System.Globalization;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Stock;
using Npgsql;
using NpgsqlTypes;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed reads and manual writes of the stock of the SELECTED branch. The on-hand is never stored: every read is
/// SUM(quantity) over `stock_movements` (append-only, org + branch RLS, index `stock_movements_on_hand_idx`). Movements
/// derived from documents (receptions, voids, synced sales) are written by <see cref="StockMovementWriter"/> in their own
/// transactions; this store only registers MANUAL movements (no source) and the minimum levels.
/// </summary>
public sealed class PostgresStockStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStockStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static Guid RequireBranch(CloudTenantScope scope) =>
        scope.BranchId ?? throw new InvalidOperationException("Stock is branch-owned: a branch must be selected.");

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Cloud -> branch stock replica (`GET /device/stock/sync`). Returns the absolute DERIVED on-hand of every presentation
    /// of the scoped branch that has a movement created at or after <paramref name="since"/> minus a grace window.
    /// <para>
    /// WHY created_at_utc + grace: the cursor is a timestamp (as for customers/catalog) and `created_at_utc` is `now()` of
    /// the transaction that wrote the movement, i.e. its START. A slow transaction can commit after a later cursor was
    /// issued and would be skipped forever; re-reading the last <see cref="ReplicaGrace"/> is safe because every item is an
    /// absolute snapshot (idempotent upsert on the branch). Cost: one index range scan (`stock_movements_created_idx`,
    /// 0034) plus the SUM of only the changed presentations (`stock_movements_on_hand_idx`), so a sweep with no news is
    /// near-free and nothing is stored or maintained per presentation.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<StockReplicaRow>> ListOnHandChangedSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT m.presentation_id, SUM(m.quantity)
            FROM stock_movements m
            WHERE m.organization_id = $1 AND m.branch_id = $2
              AND m.presentation_id IN (
                  SELECT DISTINCT presentation_id FROM stock_movements
                  WHERE organization_id = $1 AND branch_id = $2 AND created_at_utc >= $3::timestamptz - make_interval(secs => $4))
            GROUP BY m.presentation_id
            ORDER BY m.presentation_id
            """, connection, tx);
        cmd.Parameters.AddWithValue(scope.OrganizationId);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, since);
        cmd.Parameters.AddWithValue(ReplicaGrace.TotalSeconds);

        var rows = new List<StockReplicaRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new StockReplicaRow(reader.GetGuid(0), reader.GetDecimal(1)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>How far before the cursor the replica query looks back (see <see cref="ListOnHandChangedSinceAsync"/>).</summary>
    public static readonly TimeSpan ReplicaGrace = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<StockLevelRecord>> ListLevelsAsync(
        CloudTenantScope scope, StockLevelFilter filter, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.id, p.product_id, pr.name, p.name, p.quantity_behavior, p.unit_id, p.identification_code,
                   COALESCE(s.on_hand, 0), m.minimum_quantity, s.last_at
            FROM presentations p
            JOIN products pr ON pr.id = p.product_id
            LEFT JOIN (
                SELECT presentation_id, SUM(quantity) AS on_hand, MAX(occurred_at_utc) AS last_at
                FROM stock_movements
                WHERE organization_id = $1 AND branch_id = $2
                GROUP BY presentation_id) s ON s.presentation_id = p.id
            LEFT JOIN stock_minimums m
                   ON m.organization_id = $1 AND m.branch_id = $2 AND m.presentation_id = p.id
            WHERE p.organization_id = $1 AND p.branch_id = $2
              AND ($3::text IS NULL OR pr.name ILIKE $3 OR p.name ILIKE $3 OR p.identification_code ILIKE $3)
              AND (NOT $4 OR (m.minimum_quantity IS NOT NULL AND COALESCE(s.on_hand, 0) < m.minimum_quantity))
            ORDER BY pr.name, p.name, p.id
            """, connection, tx);
        cmd.Parameters.AddWithValue(scope.OrganizationId);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.Text, string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : $"%{EscapeLike(filter.Search.Trim())}%");
        cmd.Parameters.AddWithValue(filter.OnlyBelowMinimum);

        var levels = new List<StockLevelRecord>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var onHand = reader.GetDecimal(7);
                decimal? minimum = reader.IsDBNull(8) ? null : reader.GetDecimal(8);
                var below = StockRules.IsBelowMinimum(onHand, minimum);
                levels.Add(new StockLevelRecord(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetString(6), onHand, minimum, below,
                    below ? minimum - onHand : null,
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
            }
        }

        await tx.CommitAsync(ct);
        return levels;
    }

    /// <summary>The movement history of a presentation, newest first, with the running balance. Null when the presentation is not in the branch.</summary>
    public async Task<StockHistoryPage?> HistoryAsync(
        CloudTenantScope scope, Guid presentationId, int page, int pageSize, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        if (await FindBehaviorAsync(connection, tx, presentationId, ct) is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        int total;
        decimal onHand;
        await using (var totals = new NpgsqlCommand(
            "SELECT count(*)::int, COALESCE(SUM(quantity), 0) FROM stock_movements WHERE organization_id = $1 AND branch_id = $2 AND presentation_id = $3",
            connection, tx))
        {
            totals.Parameters.AddWithValue(scope.OrganizationId);
            totals.Parameters.AddWithValue(branchId);
            totals.Parameters.AddWithValue(presentationId);
            await using var reader = await totals.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            total = reader.GetInt32(0);
            onHand = reader.GetDecimal(1);
        }

        var items = new List<StockMovementRecord>();
        await using (var cmd = new NpgsqlCommand(
            """
            WITH ledger AS (
                SELECT m.*, SUM(m.quantity) OVER (ORDER BY m.occurred_at_utc, m.created_at_utc, m.id) AS balance_after
                FROM stock_movements m
                WHERE m.organization_id = $1 AND m.branch_id = $2 AND m.presentation_id = $3)
            SELECT l.id, l.kind, l.quantity, l.occurred_at_utc, l.reason, l.lot_code, l.source_type, l.source_id, r.number,
                   l.reverses_movement_id, l.created_by_user_id, l.balance_after
            FROM ledger l
            LEFT JOIN purchase_receptions r
                   ON r.id = l.source_id AND l.source_type IN ('PurchaseReception', 'PurchaseReceptionVoid')
            ORDER BY l.occurred_at_utc DESC, l.created_at_utc DESC, l.id DESC
            LIMIT $4 OFFSET $5
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(presentationId);
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue((page - 1) * pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new StockMovementRecord(
                    reader.GetGuid(0), Enum.Parse<StockMovementKind>(reader.GetString(1)), reader.GetDecimal(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    reader.GetDecimal(11)));
            }
        }

        await tx.CommitAsync(ct);
        return new StockHistoryPage(presentationId, onHand, total, page, pageSize, items);
    }

    private static async Task<QuantityBehavior?> FindBehaviorAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid presentationId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT quantity_behavior FROM presentations WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(presentationId);
        return await cmd.ExecuteScalarAsync(ct) is string value ? Enum.Parse<QuantityBehavior>(value) : null;
    }

    /// <summary>
    /// Registers a manual movement (no source document). Null when the presentation is not in the selected branch. The
    /// caller has validated sign and precision with <see cref="StockRules.TryValidateAdjustment"/> against the behavior
    /// from <see cref="FindPresentationBehaviorAsync"/>.
    /// </summary>
    public async Task<StockAdjustmentResult?> AdjustAsync(
        CloudTenantScope scope, Guid presentationId, StockMovementKind kind, decimal quantity, string reason,
        string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        if (await FindBehaviorAsync(connection, tx, presentationId, ct) is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var movementId = Guid.NewGuid();
        await StockMovementWriter.InsertAsync(
            connection, tx,
            new NewStockMovement(
                movementId, scope.OrganizationId, branchId, presentationId, quantity, kind, Reason: reason, CreatedByUserId: actorId),
            ct);

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "stock_movement", movementId, "stock.adjusted", OldValueJson: null,
                NewValueJson:
                $$"""{"presentationId":"{{presentationId}}","kind":"{{kind}}","quantity":{{quantity.ToString(CultureInfo.InvariantCulture)}}}"""),
            ct);

        var history = await HistoryInTransactionAsync(connection, tx, scope.OrganizationId, branchId, presentationId, movementId, ct);
        await tx.CommitAsync(ct);
        return history;
    }

    private static async Task<StockAdjustmentResult> HistoryInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, Guid presentationId,
        Guid movementId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT m.id, m.kind, m.quantity, m.occurred_at_utc, m.reason, m.lot_code, m.created_by_user_id,
                   (SELECT COALESCE(SUM(quantity), 0) FROM stock_movements
                     WHERE organization_id = $1 AND branch_id = $2 AND presentation_id = $3)
            FROM stock_movements m WHERE m.id = $4
            """, connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(presentationId);
        cmd.Parameters.AddWithValue(movementId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var onHand = reader.GetDecimal(7);
        var movement = new StockMovementRecord(
            reader.GetGuid(0), Enum.Parse<StockMovementKind>(reader.GetString(1)), reader.GetDecimal(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), null, null, null, null,
            reader.IsDBNull(6) ? null : reader.GetGuid(6), onHand);
        return new StockAdjustmentResult(movement, onHand);
    }

    /// <summary>The presentation's behavior (to validate a minimum) or null when it is not in the selected branch.</summary>
    public async Task<QuantityBehavior?> FindPresentationBehaviorAsync(CloudTenantScope scope, Guid presentationId, CancellationToken ct)
    {
        RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var behavior = await FindBehaviorAsync(connection, tx, presentationId, ct);
        await tx.CommitAsync(ct);
        return behavior;
    }

    /// <summary>
    /// Sets (or, with null, clears) the minimum level of a presentation. Null when the presentation is not in the branch.
    /// Setting twice updates the same row.
    /// </summary>
    public async Task<StockMinimumRecord?> SetMinimumAsync(
        CloudTenantScope scope, Guid presentationId, decimal? minimum, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        if (await FindBehaviorAsync(connection, tx, presentationId, ct) is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        StockMinimumRecord record;
        if (minimum is { } value)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO stock_minimums (organization_id, branch_id, presentation_id, minimum_quantity, updated_by_user_id)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (organization_id, branch_id, presentation_id)
                DO UPDATE SET minimum_quantity = EXCLUDED.minimum_quantity, updated_at_utc = now(),
                              updated_by_user_id = EXCLUDED.updated_by_user_id
                RETURNING minimum_quantity, updated_at_utc
                """, connection, tx);
            upsert.Parameters.AddWithValue(scope.OrganizationId);
            upsert.Parameters.AddWithValue(branchId);
            upsert.Parameters.AddWithValue(presentationId);
            upsert.Parameters.AddWithValue(value);
            upsert.Parameters.AddWithValue(actorId);
            await using var reader = await upsert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = new StockMinimumRecord(presentationId, reader.GetDecimal(0), reader.GetFieldValue<DateTimeOffset>(1));
        }
        else
        {
            await using var delete = new NpgsqlCommand(
                "DELETE FROM stock_minimums WHERE organization_id = $1 AND branch_id = $2 AND presentation_id = $3", connection, tx);
            delete.Parameters.AddWithValue(scope.OrganizationId);
            delete.Parameters.AddWithValue(branchId);
            delete.Parameters.AddWithValue(presentationId);
            await delete.ExecuteNonQueryAsync(ct);
            record = new StockMinimumRecord(presentationId, null, null);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "stock_minimum", presentationId, "stock.minimum_set", OldValueJson: null,
                NewValueJson: $$"""{"minimum":{{(minimum is { } m ? m.ToString(CultureInfo.InvariantCulture) : "null")}}}"""),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }
}
