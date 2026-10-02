using System.Globalization;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Purchasing;
using Commerce.Domain.Stock;
using Commerce.Domain.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store of goods receptions (`purchase_receptions` + `purchase_reception_lines`, branch RLS). Every method
/// opens its own transaction and applies the org AND branch scope first (the scope MUST carry a branch; the endpoints
/// enforce it). A reception of another branch or organization is invisible (NotFound).
/// <para>
/// CONFIRM and VOID are each ONE transaction: the reception row is locked (`FOR UPDATE`, so two confirmations cannot
/// race), and the status change, the number, the stock movements (<see cref="StockMovementWriter"/>), the cost history
/// and the supplier ledger Invoice / Reversal (<see cref="PostgresCurrentAccountStore"/>) commit together or not at all.
/// </para>
/// </summary>
public sealed class PostgresPurchaseReceptionStore
{
    /// <summary>Seed of the per-branch advisory lock that serializes the reception counter (orders use 3).</summary>
    private const int ReceptionCounterLockSeed = 4;
    private const string ReceptionSourceType = "PurchaseReception";
    private const string ReceptionVoidSourceType = "PurchaseReceptionVoid";

    private const string HeaderSelect =
        """
        SELECT r.id, r.branch_id, r.supplier_id, s.display_name, r.status, r.number, r.document_type, r.document_reference,
               r.occurred_on, r.due_on, r.notes, r.total_amount, r.ledger_invoice_movement_id, r.ledger_reversal_movement_id,
               r.void_reason, r.created_by_user_id, r.created_at_utc, r.confirmed_at_utc, r.voided_at_utc, r.updated_at_utc
        FROM purchase_receptions r
        JOIN suppliers s ON s.organization_id = r.organization_id AND s.id = r.supplier_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresPurchaseReceptionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static Guid RequireBranch(CloudTenantScope scope) =>
        scope.BranchId ?? throw new InvalidOperationException("Receptions are branch-owned: a branch must be selected.");

    private static DateOnly? NullableDate(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateOnly>(ordinal);

    private static DateTimeOffset? NullableTime(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateTimeOffset>(ordinal);

    private static ReceptionRecord ReadHeader(NpgsqlDataReader r, IReadOnlyList<ReceptionLineRecord> lines) => new(
        Id: r.GetGuid(0),
        BranchId: r.GetGuid(1),
        SupplierId: r.GetGuid(2),
        SupplierName: r.GetString(3),
        Status: Enum.Parse<ReceptionStatus>(r.GetString(4)),
        Number: r.IsDBNull(5) ? null : r.GetString(5),
        DocumentType: Enum.Parse<ReceptionDocumentType>(r.GetString(6)),
        DocumentReference: r.IsDBNull(7) ? null : r.GetString(7),
        OccurredOn: r.GetFieldValue<DateOnly>(8),
        DueOn: NullableDate(r, 9),
        Notes: r.IsDBNull(10) ? null : r.GetString(10),
        TotalAmount: r.GetDecimal(11),
        LedgerInvoiceMovementId: r.IsDBNull(12) ? null : r.GetGuid(12),
        LedgerReversalMovementId: r.IsDBNull(13) ? null : r.GetGuid(13),
        VoidReason: r.IsDBNull(14) ? null : r.GetString(14),
        CreatedByUserId: r.GetGuid(15),
        CreatedAtUtc: r.GetFieldValue<DateTimeOffset>(16),
        ConfirmedAtUtc: NullableTime(r, 17),
        VoidedAtUtc: NullableTime(r, 18),
        UpdatedAtUtc: r.GetFieldValue<DateTimeOffset>(19),
        Lines: lines);

    private static async Task<IReadOnlyList<ReceptionLineRecord>> ReadLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid receptionId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT l.id, l.presentation_id, pr.name, p.name, p.quantity_behavior, l.quantity, l.unit_cost, l.line_total,
                   l.lot_code, l.expires_on, l.sort_order
            FROM purchase_reception_lines l
            JOIN presentations p ON p.id = l.presentation_id
            JOIN products pr ON pr.id = p.product_id
            WHERE l.reception_id = $1
            ORDER BY l.sort_order, l.id
            """, connection, tx);
        cmd.Parameters.AddWithValue(receptionId);
        var lines = new List<ReceptionLineRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lines.Add(new ReceptionLineRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), NullableDate(reader, 9), reader.GetInt32(10)));
        }

        return lines;
    }

    private static async Task<ReceptionRecord?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"{HeaderSelect} WHERE r.id = $1 {(forUpdate ? "FOR UPDATE OF r" : string.Empty)}", connection, tx);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var header = ReadHeader(reader, []);
        await reader.CloseAsync();
        return header with { Lines = await ReadLinesAsync(connection, tx, id, ct) };
    }

    public async Task<ReceptionRecord?> FindAsync(CloudTenantScope scope, Guid id, CancellationToken ct)
    {
        RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
        var reception = await ReadAsync(connection, tx, id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        return reception;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<ReceptionSummaryRecord>> ListAsync(
        CloudTenantScope scope, ReceptionListFilter filter, CancellationToken ct)
    {
        RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.id, r.supplier_id, s.display_name, r.status, r.number, r.document_type, r.document_reference,
                   r.occurred_on, r.due_on, r.total_amount,
                   (SELECT count(*) FROM purchase_reception_lines l WHERE l.reception_id = r.id)::int,
                   r.created_at_utc, r.updated_at_utc
            FROM purchase_receptions r
            JOIN suppliers s ON s.organization_id = r.organization_id AND s.id = r.supplier_id
            WHERE ($1::text IS NULL OR r.status = $1)
              AND ($2::uuid IS NULL OR r.supplier_id = $2)
              AND ($3::date IS NULL OR r.occurred_on >= $3)
              AND ($4::date IS NULL OR r.occurred_on <= $4)
              AND ($5::text IS NULL OR r.number ILIKE $5 OR r.document_reference ILIKE $5)
            ORDER BY r.occurred_on DESC, r.created_at_utc DESC, r.id
            """, connection, tx);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Status?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)filter.SupplierId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)filter.From ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)filter.To ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : $"%{EscapeLike(filter.Search.Trim())}%");

        var rows = new List<ReceptionSummaryRecord>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new ReceptionSummaryRecord(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), Enum.Parse<ReceptionStatus>(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4), Enum.Parse<ReceptionDocumentType>(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateOnly>(7), NullableDate(reader, 8),
                    reader.GetDecimal(9), reader.GetInt32(10), reader.GetFieldValue<DateTimeOffset>(11),
                    reader.GetFieldValue<DateTimeOffset>(12)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task<ReceptionWriteResult> CreateDraftAsync(
        CloudTenantScope scope, Guid id, ReceptionContent content, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        if (await ValidateAsync(connection, tx, content, ct) is { } invalid)
        {
            await tx.RollbackAsync(ct);
            return invalid;
        }

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO purchase_receptions
                (id, organization_id, branch_id, supplier_id, status, document_type, document_reference, occurred_on, due_on,
                 notes, total_amount, created_by_user_id)
            VALUES ($1, $2, $3, $4, 'Draft', $5, $6, $7, $8, $9, $10, $11)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(content.SupplierId);
            cmd.Parameters.AddWithValue(content.DocumentType.ToString());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)content.DocumentReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Date, content.OccurredOn);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)content.DueOn ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)content.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ReceptionRules.Total(content.Lines.Select(l => (l.Quantity, l.UnitCost))));
            cmd.Parameters.AddWithValue(actorId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertLinesAsync(connection, tx, scope.OrganizationId, branchId, id, content.Lines, ct);
        await AuditAsync(connection, tx, scope, actorKind, actorId, id, "purchase_reception.drafted", "Draft", null, content.Lines.Count, ct);

        var saved = await ReadAsync(connection, tx, id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        return new ReceptionWriteResult(ReceptionWriteOutcome.Saved, saved);
    }

    /// <summary>Replaces the content (header and the WHOLE set of lines) of a draft. `expectedUpdatedAtUtc` is the optimistic token.</summary>
    public async Task<ReceptionWriteResult> UpdateDraftAsync(
        CloudTenantScope scope, Guid id, ReceptionContent content, DateTimeOffset? expectedUpdatedAtUtc, string actorKind,
        Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var current = await ReadAsync(connection, tx, id, forUpdate: true, ct);
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionWriteResult(ReceptionWriteOutcome.NotFound);
        }

        if (current.Status != ReceptionStatus.Draft)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionWriteResult(ReceptionWriteOutcome.NotDraft);
        }

        if (expectedUpdatedAtUtc is { } expected && expected.UtcTicks != current.UpdatedAtUtc.UtcTicks)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionWriteResult(ReceptionWriteOutcome.Modified);
        }

        if (await ValidateAsync(connection, tx, content, ct) is { } invalid)
        {
            await tx.RollbackAsync(ct);
            return invalid;
        }

        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE purchase_receptions
            SET supplier_id = $2, document_type = $3, document_reference = $4, occurred_on = $5, due_on = $6, notes = $7,
                total_amount = $8, updated_at_utc = clock_timestamp()
            WHERE id = $1
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(content.SupplierId);
            cmd.Parameters.AddWithValue(content.DocumentType.ToString());
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)content.DocumentReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Date, content.OccurredOn);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)content.DueOn ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)content.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ReceptionRules.Total(content.Lines.Select(l => (l.Quantity, l.UnitCost))));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM purchase_reception_lines WHERE reception_id = $1", connection, tx))
        {
            delete.Parameters.AddWithValue(id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await InsertLinesAsync(connection, tx, scope.OrganizationId, branchId, id, content.Lines, ct);
        await AuditAsync(connection, tx, scope, actorKind, actorId, id, "purchase_reception.updated", "Draft", null, content.Lines.Count, ct);

        var saved = await ReadAsync(connection, tx, id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        return new ReceptionWriteResult(ReceptionWriteOutcome.Saved, saved);
    }

    /// <summary>The supplier must exist and be enabled; every line needs a presentation of the branch and valid quantity and cost.</summary>
    private static async Task<ReceptionWriteResult?> ValidateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, ReceptionContent content, CancellationToken ct)
    {
        await using (var supplier = new NpgsqlCommand("SELECT is_enabled FROM suppliers WHERE id = $1", connection, tx))
        {
            supplier.Parameters.AddWithValue(content.SupplierId);
            var enabled = await supplier.ExecuteScalarAsync(ct);
            if (enabled is not bool isEnabled || !isEnabled)
            {
                return new ReceptionWriteResult(
                    ReceptionWriteOutcome.SupplierNotFound, Field: "supplierId",
                    Message: "supplierId must be an enabled supplier of the organization.");
            }
        }

        var behaviors = new Dictionary<Guid, QuantityBehavior>();
        var ids = content.Lines.Select(l => l.PresentationId).Distinct().ToArray();
        await using (var cmd = new NpgsqlCommand(
            // A presentation of a soft-deleted product (0036) is not receivable: it reads as not found.
            "SELECT p.id, p.quantity_behavior FROM presentations p JOIN products pr ON pr.id = p.product_id WHERE p.id = ANY($1) AND pr.is_active",
            connection, tx))
        {
            cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                behaviors[reader.GetGuid(0)] = Enum.Parse<QuantityBehavior>(reader.GetString(1));
            }
        }

        for (var i = 0; i < content.Lines.Count; i++)
        {
            var line = content.Lines[i];
            if (!behaviors.TryGetValue(line.PresentationId, out var behavior))
            {
                return Invalid(i, "presentationId", "presentationId must be a presentation of the selected branch.");
            }

            if (!ReceptionRules.TryValidateQuantity(behavior, line.Quantity, out var quantityError))
            {
                return Invalid(i, "quantity", quantityError!);
            }

            if (!ReceptionRules.TryValidateUnitCost(line.UnitCost, out var costError))
            {
                return Invalid(i, "unitCost", costError!);
            }
        }

        return null;
    }

    private static ReceptionWriteResult Invalid(int index, string field, string message) =>
        new(ReceptionWriteOutcome.InvalidLine, Field: $"lines[{index}].{field}", Message: message);

    private static async Task InsertLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, Guid receptionId,
        IReadOnlyList<NewReceptionLine> lines, CancellationToken ct)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO purchase_reception_lines
                    (id, organization_id, branch_id, reception_id, presentation_id, quantity, unit_cost, line_total, lot_code,
                     expires_on, sort_order)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """, connection, tx);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(receptionId);
            cmd.Parameters.AddWithValue(line.PresentationId);
            cmd.Parameters.AddWithValue(line.Quantity);
            cmd.Parameters.AddWithValue(line.UnitCost);
            cmd.Parameters.AddWithValue(ReceptionRules.LineTotal(line.Quantity, line.UnitCost));
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)line.LotCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)line.ExpiresOn ?? DBNull.Value);
            cmd.Parameters.AddWithValue(i);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Draft -> Confirmed in ONE transaction: assigns the next `R{branch}-W-{sequence}` number, appends one
    /// PurchaseReceipt movement and one cost-history row per line, and posts the supplier Invoice (the total, due on the
    /// reception's due date or its date plus the supplier's payment terms; skipped when the total is zero). A document
    /// already confirmed for the same supplier is refused (DuplicateDocument) and nothing is written.
    /// </summary>
    public async Task<ReceptionConfirmResult> ConfirmAsync(
        CloudTenantScope scope, Guid id, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var reception = await ReadAsync(connection, tx, id, forUpdate: true, ct);
        if (reception is null)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionConfirmResult(ReceptionConfirmOutcome.NotFound);
        }

        if (reception.Status != ReceptionStatus.Draft)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionConfirmResult(ReceptionConfirmOutcome.NotDraft);
        }

        if (reception.Lines.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionConfirmResult(ReceptionConfirmOutcome.NoLines);
        }

        int? paymentTermsDays = null;
        await using (var supplier = new NpgsqlCommand("SELECT payment_terms_days FROM suppliers WHERE id = $1", connection, tx))
        {
            supplier.Parameters.AddWithValue(reception.SupplierId);
            var terms = await supplier.ExecuteScalarAsync(ct);
            paymentTermsDays = terms is int days ? days : null;
        }

        var number = await NextNumberAsync(connection, tx, scope.OrganizationId, branchId, ct);
        var dueOn = reception.DueOn
            ?? CurrentAccountRules.DefaultDueOn(AccountMovementKind.Invoice, reception.OccurredOn, paymentTermsDays);
        var invoiceId = reception.TotalAmount > 0 ? Guid.NewGuid() : (Guid?)null;

        try
        {
            await using var update = new NpgsqlCommand(
                """
                UPDATE purchase_receptions
                SET status = 'Confirmed', branch_code = $2, sequence = $3, number = $4, due_on = $5,
                    ledger_invoice_movement_id = $6, confirmed_by_user_id = $7, confirmed_at_utc = now(),
                    updated_at_utc = clock_timestamp()
                WHERE id = $1
                """, connection, tx);
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue((short)number.Branch.Value);
            update.Parameters.AddWithValue(number.Sequence);
            update.Parameters.AddWithValue(number.Format());
            update.Parameters.AddWithValue(NpgsqlDbType.Date, (object?)dueOn ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)invoiceId ?? DBNull.Value);
            update.Parameters.AddWithValue(actorId);
            await update.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "purchase_receptions_confirmed_document_uk")
        {
            await tx.RollbackAsync(ct);
            return new ReceptionConfirmResult(ReceptionConfirmOutcome.DuplicateDocument);
        }

        foreach (var line in reception.Lines)
        {
            await StockMovementWriter.InsertAsync(
                connection, tx,
                new NewStockMovement(
                    Guid.NewGuid(), scope.OrganizationId, branchId, line.PresentationId, line.Quantity,
                    StockMovementKind.PurchaseReceipt, ReceptionSourceType, id, line.Id, LotCode: line.LotCode,
                    CreatedByUserId: actorId),
                ct);

            await using var cost = new NpgsqlCommand(
                """
                INSERT INTO presentation_costs
                    (id, organization_id, branch_id, presentation_id, supplier_id, reception_id, reception_line_id, unit_cost, occurred_on)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                """, connection, tx);
            cost.Parameters.AddWithValue(Guid.NewGuid());
            cost.Parameters.AddWithValue(scope.OrganizationId);
            cost.Parameters.AddWithValue(branchId);
            cost.Parameters.AddWithValue(line.PresentationId);
            cost.Parameters.AddWithValue(reception.SupplierId);
            cost.Parameters.AddWithValue(id);
            cost.Parameters.AddWithValue(line.Id);
            cost.Parameters.AddWithValue(line.UnitCost);
            cost.Parameters.AddWithValue(NpgsqlDbType.Date, reception.OccurredOn);
            await cost.ExecuteNonQueryAsync(ct);
        }

        if (invoiceId is { } movementId)
        {
            var invoice = new NewAccountMovement(
                movementId, AccountMovementKind.Invoice, AccountDirection.Credit, reception.TotalAmount, reception.OccurredOn,
                dueOn, reception.DocumentReference ?? number.Format(), $"Recepción de mercadería {number.Format()}", actorId);
            var record = await PostgresCurrentAccountStore.InsertAsync(
                connection, tx, scope.OrganizationId, reception.SupplierId, invoice, null, ct);
            await PostgresCurrentAccountStore.AuditAsync(
                connection, tx, scope, actorKind, actorId, reception.SupplierId, "supplier.movement_registered", record, ct);
        }

        await AuditAsync(
            connection, tx, scope, actorKind, actorId, id, "purchase_reception.confirmed", number.Format(), reception.TotalAmount,
            reception.Lines.Count, ct);

        var confirmed = await ReadAsync(connection, tx, id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        return new ReceptionConfirmResult(ReceptionConfirmOutcome.Confirmed, confirmed);
    }

    /// <summary>
    /// Confirmed -> Voided in ONE transaction: a compensating `Reversal` stock movement per received line (the original
    /// movements stay) and the Reversal of the supplier Invoice. The number stays assigned and frees the supplier document.
    /// </summary>
    public async Task<ReceptionVoidResult> VoidAsync(
        CloudTenantScope scope, Guid id, string reason, DateOnly today, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = RequireBranch(scope);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var reception = await ReadAsync(connection, tx, id, forUpdate: true, ct);
        if (reception is null)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionVoidResult(ReceptionVoidOutcome.NotFound);
        }

        if (reception.Status != ReceptionStatus.Confirmed)
        {
            await tx.RollbackAsync(ct);
            return new ReceptionVoidResult(ReceptionVoidOutcome.NotConfirmed);
        }

        var originals = new List<(Guid Id, Guid PresentationId, decimal Quantity, Guid? LineId, string? Lot)>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, presentation_id, quantity, source_line_id, lot_code
            FROM stock_movements
            WHERE source_type = $1 AND source_id = $2 AND kind = 'PurchaseReceipt'
            ORDER BY created_at_utc, id
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(ReceptionSourceType);
            cmd.Parameters.AddWithValue(id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                originals.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        foreach (var original in originals)
        {
            await StockMovementWriter.InsertAsync(
                connection, tx,
                new NewStockMovement(
                    Guid.NewGuid(), scope.OrganizationId, branchId, original.PresentationId, -original.Quantity,
                    StockMovementKind.Reversal, ReceptionVoidSourceType, id, original.LineId, original.Id, reason,
                    original.Lot, actorId),
                ct);
        }

        Guid? reversalId = null;
        if (reception.LedgerInvoiceMovementId is { } invoiceId)
        {
            await tx.SaveAsync("ledger_reversal", ct);
            var reversal = await PostgresCurrentAccountStore.ReverseWithinAsync(
                connection, tx, scope.OrganizationId, reception.SupplierId, invoiceId,
                $"Anulación de recepción {reception.Number}: {reason}", null, today, actorId, ct);
            if (reversal.Outcome == ReverseMovementOutcome.Reversed)
            {
                reversalId = reversal.Reversal!.Id;
                await PostgresCurrentAccountStore.AuditAsync(
                    connection, tx, scope, actorKind, actorId, reception.SupplierId, "supplier.movement_reversed", reversal.Reversal, ct);
            }
            else if (reversal.Outcome != ReverseMovementOutcome.AlreadyReversed)
            {
                await tx.RollbackAsync(ct);
                throw new InvalidOperationException(
                    $"The Invoice of reception {id} could not be reversed ({reversal.Outcome}).");
            }
            // else: the Invoice was already reversed by hand from the supplier account; the savepoint undoes the
            // aborted statement and only the stock and the status move.
            else
            {
                await tx.RollbackAsync("ledger_reversal", ct);
            }
        }

        await FinishVoidAsync(connection, tx, id, reason, reversalId, actorId, ct);
        await AuditAsync(
            connection, tx, scope, actorKind, actorId, id, "purchase_reception.voided", reception.Number ?? string.Empty,
            reception.TotalAmount, reception.Lines.Count, ct);

        var voided = await ReadAsync(connection, tx, id, forUpdate: false, ct);
        await tx.CommitAsync(ct);
        return new ReceptionVoidResult(ReceptionVoidOutcome.Voided, voided);
    }

    private static async Task FinishVoidAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid id, string reason, Guid? reversalId, Guid actorId, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE purchase_receptions
            SET status = 'Voided', void_reason = $2, ledger_reversal_movement_id = $3, voided_by_user_id = $4,
                voided_at_utc = now(), updated_at_utc = clock_timestamp()
            WHERE id = $1
            """, connection, tx);
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(reason);
        update.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)reversalId ?? DBNull.Value);
        update.Parameters.AddWithValue(actorId);
        await update.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The next `R{branch}-W-{sequence}` of the branch, under the per-branch advisory lock (released at COMMIT).</summary>
    private static async Task<ReceptionNumber> NextNumberAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid branchId, CancellationToken ct)
    {
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, $2))", connection, tx))
        {
            lockCmd.Parameters.AddWithValue(branchId.ToString());
            lockCmd.Parameters.AddWithValue((long)ReceptionCounterLockSeed);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        int code;
        await using (var codeCmd = new NpgsqlCommand("SELECT code FROM branches WHERE organization_id = $1 AND id = $2", connection, tx))
        {
            codeCmd.Parameters.AddWithValue(organizationId);
            codeCmd.Parameters.AddWithValue(branchId);
            var value = await codeCmd.ExecuteScalarAsync(ct);
            code = value is null or DBNull
                ? throw new InvalidOperationException($"Branch {branchId} has no code.")
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(MAX(sequence), 0) + 1 FROM purchase_receptions WHERE organization_id = $1 AND branch_id = $2",
            connection, tx);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(branchId);
        var sequence = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return new ReceptionNumber(new BranchCode(code), sequence);
    }

    private static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, string actorKind, Guid actorId,
        Guid receptionId, string action, string label, object? total, int lineCount, CancellationToken ct) =>
        AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "purchase_reception", receptionId, action, OldValueJson: null,
                NewValueJson:
                $$"""{"label":"{{label}}","total":{{Convert.ToString(total ?? 0, CultureInfo.InvariantCulture)}},"lines":{{lineCount}}}"""),
            ct);
}
