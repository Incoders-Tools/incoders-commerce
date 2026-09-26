using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed price store (commerce-pricing-engine design.md
/// "Price tables RLS"), mirroring <see cref="PostgresCustomerStore"/>'s
/// exact shape: raw <see cref="NpgsqlDataSource"/>, every scoped method
/// opens its own <see cref="NpgsqlTransaction"/>, `set_config` is always the
/// FIRST statement, mutations write their audit row in the SAME
/// transaction. <see cref="AppendEntryAsync"/> is the ONLY write path onto
/// `price_list_entries` — it is a pure INSERT, never an UPDATE (design.md
/// "Effective-dating shape": append-only, no `EffectiveTo`). A same-day
/// double-publish surfaces as a <see cref="PostgresException"/> with
/// SqlState `23505` (`price_list_entries_one_per_day`) for the caller
/// (endpoint) to translate into a 409.
/// </summary>
public sealed class PostgresPriceListStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPriceListStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
    }

    // --- Price lists ------------------------------------------------------

    private const string PriceListColumns = "id, organization_id, name, is_default, created_at_utc, created_by_user_id";

    private static PriceListRecord ReadPriceList(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        Name: reader.GetString(2),
        IsDefault: reader.GetBoolean(3),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(4),
        CreatedByUserId: reader.GetGuid(5));

    /// <summary>
    /// A second `is_default = true` list for the same organization surfaces
    /// as a <see cref="PostgresException"/> (`price_lists_one_default`) —
    /// the caller translates it into a 409.
    /// </summary>
    public async Task<PriceListRecord> CreatePriceListAsync(
        CloudTenantScope scope, NewPriceList priceList, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        PriceListRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5)
            RETURNING {PriceListColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(priceList.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(priceList.Name);
            cmd.Parameters.AddWithValue(priceList.IsDefault);
            cmd.Parameters.AddWithValue(priceList.CreatedByUserId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = ReadPriceList(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "price_list", priceList.Id, "price-list.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<PriceListRecord?> FindPriceListAsync(CloudTenantScope scope, Guid priceListId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {PriceListColumns} FROM price_lists WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(priceListId);

        PriceListRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadPriceList(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>The org's single default list, or null if none has been created yet.</summary>
    public async Task<PriceListRecord?> FindDefaultPriceListAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {PriceListColumns} FROM price_lists WHERE is_default", connection, tx);

        PriceListRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadPriceList(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<PriceListRecord>> ListPriceListsAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<PriceListRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PriceListColumns} FROM price_lists ORDER BY name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadPriceList(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    // --- Price list entries (append-only) ----------------------------------

    private const string EntryColumns =
        "id, organization_id, price_list_id, presentation_id, unit_price, effective_from, source, " +
        "import_batch_id, created_at_utc, created_by_user_id";

    private static PriceListEntryRecord ReadEntry(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        PriceListId: reader.GetGuid(2),
        PresentationId: reader.GetGuid(3),
        UnitPrice: reader.GetDecimal(4),
        EffectiveFrom: DateOnly.FromDateTime(reader.GetDateTime(5)),
        Source: reader.GetString(6),
        ImportBatchId: reader.IsDBNull(7) ? null : reader.GetGuid(7),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(8),
        CreatedByUserId: reader.GetGuid(9));

    /// <summary>
    /// The ONLY write path onto `price_list_entries` — a pure INSERT.
    /// Publishing a new price NEVER rewrites a prior entry (design.md
    /// "Effective-dating shape"). A same-day double-publish for the same
    /// (price list, presentation) throws (`price_list_entries_one_per_day`).
    /// </summary>
    public async Task<PriceListEntryRecord> AppendEntryAsync(
        CloudTenantScope scope, NewPriceListEntry entry, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var record = await AppendEntryCoreAsync(connection, tx, scope, entry, actorKind, actorId, ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// The actual INSERT + audit write, WITHOUT opening its own
    /// connection/transaction or committing — this is what makes
    /// <see cref="CommitImportBatchAsync"/> able to append several entries
    /// through the SAME transaction as the batch's own status update
    /// (design.md "no bulk-write bypass": every entry still goes through
    /// this exact write path, just not through a separate transaction per
    /// row). The caller is responsible for `set_config` and commit/rollback.
    /// </summary>
    private static async Task<PriceListEntryRecord> AppendEntryCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope,
        NewPriceListEntry entry, string actorKind, Guid actorId, CancellationToken ct)
    {
        PriceListEntryRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO price_list_entries
                (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, source, import_batch_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING {EntryColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(entry.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(entry.PriceListId);
            cmd.Parameters.AddWithValue(entry.PresentationId);
            cmd.Parameters.AddWithValue(entry.UnitPrice);
            cmd.Parameters.AddWithValue(entry.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue(entry.Source);
            cmd.Parameters.AddWithValue((object?)entry.ImportBatchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(entry.CreatedByUserId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = ReadEntry(reader);
        }

        // InvariantCulture: a locale using ',' as the decimal separator (a
        // real deployment concern for an Argentine-market product) would
        // otherwise emit "100,00" here and break the JSON the DB expects.
        var unitPriceInvariant = entry.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "price_list_entry", entry.Id, "price-list-entry.published",
                OldValueJson: null,
                NewValueJson: $$"""{"unitPrice":{{unitPriceInvariant}},"effectiveFrom":"{{entry.EffectiveFrom:yyyy-MM-dd}}"}"""),
            ct);

        return record;
    }

    /// <summary>
    /// The resolution query (design.md "Effective-dating shape"): the latest
    /// entry on or before <paramref name="effectiveOn"/>, or null for
    /// exactly zero matching rows — "no effective price for this date" is
    /// the caller's responsibility to translate into a typed outcome, never
    /// a zero substitution.
    /// </summary>
    public async Task<PriceListEntryRecord?> GetEffectiveAsync(
        CloudTenantScope scope, Guid priceListId, Guid presentationId, DateOnly effectiveOn, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {EntryColumns} FROM price_list_entries
            WHERE price_list_id = $1 AND presentation_id = $2 AND effective_from <= $3
            ORDER BY effective_from DESC
            LIMIT 1
            """, connection, tx);
        cmd.Parameters.AddWithValue(priceListId);
        cmd.Parameters.AddWithValue(presentationId);
        cmd.Parameters.AddWithValue(effectiveOn.ToDateTime(TimeOnly.MinValue));

        PriceListEntryRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadEntry(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>Every entry ever published for a presentation, newest first — the admin history view.</summary>
    public async Task<IReadOnlyList<PriceListEntryRecord>> ListHistoryAsync(
        CloudTenantScope scope, Guid priceListId, Guid presentationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<PriceListEntryRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT {EntryColumns} FROM price_list_entries
            WHERE price_list_id = $1 AND presentation_id = $2
            ORDER BY effective_from DESC
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(priceListId);
            cmd.Parameters.AddWithValue(presentationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadEntry(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// The currently-effective entry (design.md "Effective-dating shape") for
    /// every presentation whose price CHANGED since <paramref name="since"/> —
    /// i.e. a new entry was published after the cursor — for
    /// <c>GET /device/catalog/sync</c> (design.md "BranchNode replication").
    /// `DISTINCT ON` picks, per presentation, the entry effective on or before
    /// <paramref name="effectiveOn"/> with the latest `effective_from`; the
    /// `created_at_utc &gt; since` filter is what makes this a CHANGE set
    /// rather than the full effective snapshot.
    /// </summary>
    public async Task<IReadOnlyList<PriceListEntryRecord>> ListEffectiveChangedSinceAsync(
        CloudTenantScope scope, Guid priceListId, DateTimeOffset since, DateOnly effectiveOn, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<PriceListEntryRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT DISTINCT ON (presentation_id) {EntryColumns} FROM price_list_entries
            WHERE price_list_id = $1 AND effective_from <= $2 AND created_at_utc > $3
            ORDER BY presentation_id, effective_from DESC
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(priceListId);
            cmd.Parameters.AddWithValue(effectiveOn.ToDateTime(TimeOnly.MinValue));
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadEntry(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    // --- Supplier price import (Work Unit 9) -------------------------------

    private const string SupplierMappingColumns =
        "id, organization_id, supplier_name, sheet_name, header_row, code_column, price_column, created_at_utc, created_by_user_id";

    private static SupplierPriceMappingRecord ReadSupplierMapping(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        SupplierName: reader.GetString(2),
        SheetName: reader.GetString(3),
        HeaderRow: reader.GetInt32(4),
        CodeColumn: reader.GetString(5),
        PriceColumn: reader.GetString(6),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7),
        CreatedByUserId: reader.GetGuid(8));

    /// <summary>
    /// A second mapping with the same `supplier_name` for this organization
    /// is rejected by `supplier_price_mappings_org_name_uk` — surfaced as a
    /// <see cref="PostgresException"/> (`23505`) for the caller to translate
    /// into a 409 (design.md "Per-supplier column mapping": one saved
    /// mapping per supplier, reused for every later import).
    /// </summary>
    public async Task<SupplierPriceMappingRecord> CreateSupplierMappingAsync(
        CloudTenantScope scope, NewSupplierPriceMapping mapping, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        SupplierPriceMappingRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO supplier_price_mappings
                (id, organization_id, supplier_name, sheet_name, header_row, code_column, price_column, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING {SupplierMappingColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(mapping.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(mapping.SupplierName);
            cmd.Parameters.AddWithValue(mapping.SheetName);
            cmd.Parameters.AddWithValue(mapping.HeaderRow);
            cmd.Parameters.AddWithValue(mapping.CodeColumn);
            cmd.Parameters.AddWithValue(mapping.PriceColumn);
            cmd.Parameters.AddWithValue(mapping.CreatedByUserId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = ReadSupplierMapping(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "supplier_price_mapping", mapping.Id, "supplier-price-mapping.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<SupplierPriceMappingRecord?> FindSupplierMappingAsync(CloudTenantScope scope, Guid mappingId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {SupplierMappingColumns} FROM supplier_price_mappings WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(mappingId);

        SupplierPriceMappingRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadSupplierMapping(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<SupplierPriceMappingRecord>> ListSupplierMappingsAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<SupplierPriceMappingRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {SupplierMappingColumns} FROM supplier_price_mappings ORDER BY supplier_name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadSupplierMapping(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    private const string ImportBatchColumns =
        "id, organization_id, supplier_mapping_id, file_name, row_count, status, uploaded_at_utc, uploaded_by_user_id, resolved_at_utc";

    private static ImportBatchRecord ReadImportBatch(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        SupplierMappingId: reader.GetGuid(2),
        FileName: reader.GetString(3),
        RowCount: reader.GetInt32(4),
        Status: reader.GetString(5),
        UploadedAtUtc: reader.GetFieldValue<DateTimeOffset>(6),
        UploadedByUserId: reader.GetGuid(7),
        ResolvedAtUtc: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    private const string ImportRowColumns =
        "id, organization_id, batch_id, row_number, raw_code, raw_price, presentation_id, current_price, proposed_price, match_status, reject_reason";

    private static ImportBatchRowRecord ReadImportRow(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        BatchId: reader.GetGuid(2),
        RowNumber: reader.GetInt32(3),
        RawCode: reader.IsDBNull(4) ? null : reader.GetString(4),
        RawPrice: reader.IsDBNull(5) ? null : reader.GetString(5),
        PresentationId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
        CurrentPrice: reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        ProposedPrice: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        MatchStatus: reader.GetString(9),
        RejectReason: reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>
    /// ONE transaction: INSERT the batch (`status = 'Staged'` — design.md:
    /// deliberately no `Uploaded` state) then every row. A file that failed
    /// <c>ImportGuards</c>/parsing never reaches this method at all, so no
    /// batch row is ever created for a rejected upload (design.md "Uploaded
    /// file changes no live price before review").
    /// </summary>
    public async Task<ImportBatchRecord> CreateImportBatchAsync(
        CloudTenantScope scope, Guid supplierMappingId, string fileName, IReadOnlyList<NewImportBatchRow> rows,
        string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        ImportBatchRecord batch;
        var batchId = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO price_import_batches
                (id, organization_id, supplier_mapping_id, file_name, row_count, uploaded_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            RETURNING {ImportBatchColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(supplierMappingId);
            cmd.Parameters.AddWithValue(fileName);
            cmd.Parameters.AddWithValue(rows.Count);
            cmd.Parameters.AddWithValue(actorId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            batch = ReadImportBatch(reader);
        }

        foreach (var row in rows)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO price_import_rows
                    (id, organization_id, batch_id, row_number, raw_code, raw_price, presentation_id, current_price, proposed_price, match_status)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """, connection, tx);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(batchId);
            cmd.Parameters.AddWithValue(row.RowNumber);
            cmd.Parameters.AddWithValue((object?)row.RawCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.RawPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.PresentationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.CurrentPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)row.ProposedPrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue(row.MatchStatus);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "price_import_batch", batchId, "price-import-batch.staged",
                OldValueJson: null, NewValueJson: $$"""{"fileName":"{{fileName}}","rowCount":{{rows.Count}}}"""),
            ct);

        await tx.CommitAsync(ct);
        return batch;
    }

    public async Task<ImportBatchRecord?> FindImportBatchAsync(CloudTenantScope scope, Guid batchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {ImportBatchColumns} FROM price_import_batches WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(batchId);

        ImportBatchRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadImportBatch(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<ImportBatchRowRecord>> ListImportRowsAsync(CloudTenantScope scope, Guid batchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<ImportBatchRowRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ImportRowColumns} FROM price_import_rows WHERE batch_id = $1 ORDER BY row_number", connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadImportRow(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// ONE transaction: lock and load the batch (`SELECT ... FOR UPDATE`,
    /// so two concurrent commits cannot both observe `Staged`) -&gt; for
    /// every `Matched` row, append through the exact SAME
    /// <see cref="AppendEntryCoreAsync"/> write path
    /// <see cref="AppendEntryAsync"/> uses (design.md "no bulk-write
    /// bypass") -&gt; flip the batch to `Committed` -&gt; one batch-level audit
    /// row. A batch that is not currently `Staged` throws
    /// <see cref="ImportBatchNotStagedException"/> for the caller to
    /// translate into a 409 (design.md: "committing twice ⇒ 409").
    /// </summary>
    public async Task<ImportBatchRecord> CommitImportBatchAsync(
        CloudTenantScope scope, Guid batchId, Guid priceListId, DateOnly effectiveFrom,
        string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var (batch, rows) = await LoadStagedBatchForUpdateAsync(connection, tx, batchId, ct);

        foreach (var row in rows.Where(r => r.MatchStatus == nameof(Commerce.Application.Pricing.Import.ImportMatchStatus.Matched)))
        {
            await AppendEntryCoreAsync(
                connection, tx, scope,
                new NewPriceListEntry(
                    Guid.NewGuid(), priceListId, row.PresentationId!.Value, row.ProposedPrice!.Value,
                    effectiveFrom, "Import", ImportBatchId: batchId, actorId),
                actorKind, actorId, ct);
        }

        ImportBatchRecord committed;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE price_import_batches SET status = 'Committed', resolved_at_utc = now()
            WHERE id = $1
            RETURNING {ImportBatchColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            committed = ReadImportBatch(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "price_import_batch", batchId, "price-import-batch.committed",
                OldValueJson: $$"""{"status":"{{batch.Status}}"}""", NewValueJson: """{"status":"Committed"}"""),
            ct);

        await tx.CommitAsync(ct);
        return committed;
    }

    /// <summary>
    /// Reject leaves ZERO prices written — pure status flip, no
    /// `price_list_entries` touched at all (design.md "Reject: a rejected
    /// batch changes zero prices, full stop").
    /// </summary>
    public async Task<ImportBatchRecord> RejectImportBatchAsync(
        CloudTenantScope scope, Guid batchId, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var (batch, _) = await LoadStagedBatchForUpdateAsync(connection, tx, batchId, ct);

        ImportBatchRecord rejected;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE price_import_batches SET status = 'Rejected', resolved_at_utc = now()
            WHERE id = $1
            RETURNING {ImportBatchColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            rejected = ReadImportBatch(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "price_import_batch", batchId, "price-import-batch.rejected",
                OldValueJson: $$"""{"status":"{{batch.Status}}"}""", NewValueJson: """{"status":"Rejected"}"""),
            ct);

        await tx.CommitAsync(ct);
        return rejected;
    }

    private static async Task<(ImportBatchRecord Batch, IReadOnlyList<ImportBatchRowRecord> Rows)> LoadStagedBatchForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid batchId, CancellationToken ct)
    {
        ImportBatchRecord batch;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ImportBatchColumns} FROM price_import_batches WHERE id = $1 FOR UPDATE", connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new ImportBatchNotStagedException(batchId, "NotFound");
            }
            batch = ReadImportBatch(reader);
        }

        if (batch.Status != "Staged")
        {
            throw new ImportBatchNotStagedException(batchId, batch.Status);
        }

        var rows = new List<ImportBatchRowRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {ImportRowColumns} FROM price_import_rows WHERE batch_id = $1 ORDER BY row_number", connection, tx))
        {
            cmd.Parameters.AddWithValue(batchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadImportRow(reader));
            }
        }

        return (batch, rows);
    }
}
