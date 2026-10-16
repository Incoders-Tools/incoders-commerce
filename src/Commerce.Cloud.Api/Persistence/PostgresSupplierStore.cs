using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Npgsql;
using NpgsqlTypes;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed supplier store, shaped like <see cref="PostgresCustomerStore"/>: every method opens its own
/// transaction, `set_config` is the first statement and mutations write their audit row in the same
/// transaction. A cross-organization id is invisible under RLS (null / not found). There is no delete:
/// a supplier is disabled. The balance of each supplier is derived from the append-only ledger
/// (credits minus debits: what the business owes).
/// </summary>
public sealed class PostgresSupplierStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSupplierStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static SupplierRecord Read(NpgsqlDataReader r) => new(
        Id: r.GetGuid(0),
        OrganizationId: r.GetGuid(1),
        DisplayName: r.GetString(2),
        LegalName: Str(r, 3),
        TaxIdType: Enum.Parse<TaxIdType>(r.GetString(4)),
        TaxId: Str(r, 5),
        TaxCondition: Enum.Parse<TaxCondition>(r.GetString(6)),
        Phone: Str(r, 7),
        Email: Str(r, 8),
        AddressStreet: Str(r, 9),
        AddressNumber: Str(r, 10),
        Neighborhood: Str(r, 11),
        PostalCode: Str(r, 12),
        CityId: r.IsDBNull(13) ? null : r.GetGuid(13),
        CityName: Str(r, 14),
        ProvinceId: Str(r, 15),
        ProvinceName: Str(r, 16),
        CategoryId: r.IsDBNull(17) ? null : r.GetGuid(17),
        CategoryName: Str(r, 18),
        PaymentTermsDays: r.IsDBNull(19) ? null : r.GetInt32(19),
        BankCbu: Str(r, 20),
        BankAlias: Str(r, 21),
        Notes: Str(r, 22),
        IsEnabled: r.GetBoolean(23),
        CreatedAtUtc: r.GetFieldValue<DateTimeOffset>(24),
        CreatedByUserId: r.GetGuid(25),
        UpdatedAtUtc: r.GetFieldValue<DateTimeOffset>(26),
        Balance: r.GetDecimal(27));

    private const string SelectColumns =
        """
        s.id, s.organization_id, s.display_name, s.legal_name, s.tax_id_type, s.tax_id, s.tax_condition,
        s.phone, s.email, s.address_street, s.address_number, s.neighborhood, s.postal_code,
        s.city_id, ci.name, ci.province_id, pr.name, s.category_id, sc.name,
        s.payment_terms_days, s.bank_cbu, s.bank_alias, s.notes, s.is_enabled,
        s.created_at_utc, s.created_by_user_id, s.updated_at_utc,
        COALESCE((SELECT sum(CASE m.direction WHEN 'Credit' THEN m.amount ELSE -m.amount END)
                  FROM current_account_movements m
                  WHERE m.organization_id = s.organization_id AND m.supplier_id = s.id), 0)
        """;

    private const string FromClause =
        """
        suppliers s
        LEFT JOIN cities ci ON ci.id = s.city_id
        LEFT JOIN provinces pr ON pr.id = ci.province_id
        LEFT JOIN supplier_categories sc ON sc.organization_id = s.organization_id AND sc.id = s.category_id
        """;

    private static async Task<SupplierRecord?> SelectByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid supplierId, CancellationToken ct)
    {
        SupplierRecord? record;
        await using (var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM {FromClause} WHERE s.id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(supplierId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            record = await reader.ReadAsync(ct) ? Read(reader) : null;
        }

        if (record is null)
        {
            return null;
        }

        var contacts = await LoadContactsAsync(connection, tx, [supplierId], ct);
        return contacts.TryGetValue(supplierId, out var own) ? record with { Contacts = own } : record;
    }

    private static async Task<Dictionary<Guid, List<SupplierContactRecord>>> LoadContactsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid[] supplierIds, CancellationToken ct)
    {
        var bySupplier = new Dictionary<Guid, List<SupplierContactRecord>>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT supplier_id, id, first_name, last_name, phone, email, role, is_primary, sort_order
            FROM supplier_contacts
            WHERE supplier_id = ANY($1)
            ORDER BY sort_order, created_at_utc, id
            """, connection, tx);
        cmd.Parameters.AddWithValue(supplierIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var supplierId = reader.GetGuid(0);
            if (!bySupplier.TryGetValue(supplierId, out var list))
            {
                bySupplier[supplierId] = list = [];
            }

            list.Add(new SupplierContactRecord(
                reader.GetGuid(1), reader.GetString(2), Str(reader, 3), Str(reader, 4), Str(reader, 5), Str(reader, 6),
                reader.GetBoolean(7), reader.GetInt32(8)));
        }

        return bySupplier;
    }

    /// <summary>
    /// REPLACE-SET of one supplier's contacts inside the caller's transaction (same algorithm as the
    /// customer store): ids not sent are deleted, the primary flag is cleared and re-applied per contact,
    /// every sent contact is upserted by id. An id of another supplier or organization is refused.
    /// </summary>
    private static async Task ReplaceContactsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid supplierId,
        IReadOnlyList<SupplierContactInput> contacts, CancellationToken ct)
    {
        var keptIds = contacts.Where(c => c.Id is not null).Select(c => c.Id!.Value).ToArray();
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM supplier_contacts WHERE supplier_id = $1 AND NOT (id = ANY($2))", connection, tx))
        {
            cmd.Parameters.AddWithValue(supplierId);
            cmd.Parameters.AddWithValue(keptIds);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE supplier_contacts SET is_primary = false WHERE supplier_id = $1 AND is_primary", connection, tx))
        {
            cmd.Parameters.AddWithValue(supplierId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var contact in contacts)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO supplier_contacts
                    (id, organization_id, supplier_id, first_name, last_name, phone, email, role, is_primary, sort_order)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (id) DO UPDATE
                SET first_name = EXCLUDED.first_name, last_name = EXCLUDED.last_name, phone = EXCLUDED.phone,
                    email = EXCLUDED.email, role = EXCLUDED.role, is_primary = EXCLUDED.is_primary,
                    sort_order = EXCLUDED.sort_order, updated_at_utc = now()
                WHERE supplier_contacts.supplier_id = EXCLUDED.supplier_id
                """, connection, tx);
            cmd.Parameters.AddWithValue(contact.Id ?? Guid.NewGuid());
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(supplierId);
            cmd.Parameters.AddWithValue(contact.FirstName);
            cmd.Parameters.AddWithValue((object?)contact.LastName ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)contact.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)contact.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)contact.Role ?? DBNull.Value);
            cmd.Parameters.AddWithValue(contact.IsPrimary);
            cmd.Parameters.AddWithValue(contact.SortOrder);

            int affected;
            try
            {
                affected = await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (
                ex.TableName == "supplier_contacts"
                && ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UniqueViolation)
            {
                throw new SupplierContactRejectedException($"Contact id {contact.Id} cannot be used.");
            }

            if (affected == 0)
            {
                throw new SupplierContactRejectedException($"Contact id {contact.Id} belongs to another supplier.");
            }
        }
    }

    /// <summary>ONE transaction: INSERT suppliers, contacts, audit row ("supplier.created"), COMMIT.</summary>
    public async Task<SupplierRecord> CreateAsync(
        CloudTenantScope scope, NewSupplier supplier, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO suppliers
                (id, organization_id, display_name, legal_name, tax_id_type, tax_id, tax_condition, phone, email,
                 address_street, address_number, neighborhood, postal_code, city_id, category_id,
                 payment_terms_days, bank_cbu, bank_alias, notes, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(supplier.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(supplier.DisplayName);
            cmd.Parameters.AddWithValue((object?)supplier.LegalName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(supplier.TaxIdType.ToString());
            cmd.Parameters.AddWithValue((object?)supplier.TaxId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(supplier.TaxCondition.ToString());
            cmd.Parameters.AddWithValue((object?)supplier.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.AddressStreet ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.AddressNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.Neighborhood ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.PostalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)supplier.CityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)supplier.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)supplier.PaymentTermsDays ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.BankCbu ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.BankAlias ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)supplier.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(supplier.CreatedByUserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (supplier.Contacts is { Count: > 0 } contacts)
        {
            await ReplaceContactsAsync(connection, tx, scope.OrganizationId, supplier.Id, contacts, ct);
        }

        var record = (await SelectByIdAsync(connection, tx, supplier.Id, ct))!;

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "supplier", supplier.Id, "supplier.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// ONE transaction: read the current row (existence / cross-org check), token-checked UPDATE, contacts
    /// replace-set, audit row ("supplier.updated"). Null when the id is not visible; throws
    /// <see cref="SupplierModifiedException"/> when the optimistic token is stale (nothing is written).
    /// </summary>
    public async Task<SupplierRecord?> UpdateAsync(
        CloudTenantScope scope, Guid supplierId, UpdateSupplier update, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var existing = await SelectByIdAsync(connection, tx, supplierId, ct);
        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE suppliers
            SET display_name = $1, legal_name = $2, tax_id_type = $3, tax_id = $4, tax_condition = $5,
                phone = $6, email = $7, address_street = $8, address_number = $9, neighborhood = $10,
                postal_code = $11, payment_terms_days = $12, bank_cbu = $13, bank_alias = $14, notes = $15,
                is_enabled = COALESCE($16::boolean, is_enabled),
                city_id = CASE WHEN $17 THEN $18::uuid ELSE city_id END,
                category_id = CASE WHEN $19 THEN $20::uuid ELSE category_id END,
                updated_at_utc = now()
            WHERE id = $21 AND ($22::timestamptz IS NULL OR updated_at_utc = $22)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(update.DisplayName);
            cmd.Parameters.AddWithValue((object?)update.LegalName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.TaxIdType.ToString());
            cmd.Parameters.AddWithValue((object?)update.TaxId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.TaxCondition.ToString());
            cmd.Parameters.AddWithValue((object?)update.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.AddressStreet ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.AddressNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.Neighborhood ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.PostalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)update.PaymentTermsDays ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.BankCbu ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.BankAlias ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, (object?)update.IsEnabled ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.City is not null);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)update.City?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.Category is not null);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)update.Category?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue(supplierId);
            cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)update.ExpectedUpdatedAtUtc ?? DBNull.Value);

            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                throw new SupplierModifiedException(supplierId);
            }
        }

        if (update.Contacts is not null)
        {
            await ReplaceContactsAsync(connection, tx, scope.OrganizationId, supplierId, update.Contacts, ct);
        }

        var updated = (await SelectByIdAsync(connection, tx, supplierId, ct))!;

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "supplier", supplierId, "supplier.updated",
                OldValueJson: $$"""{"displayName":"{{System.Text.Json.JsonEncodedText.Encode(existing.DisplayName)}}"}""",
                NewValueJson: $$"""{"displayName":"{{System.Text.Json.JsonEncodedText.Encode(updated.DisplayName)}}"}"""),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<SupplierRecord?> FindAsync(CloudTenantScope scope, Guid supplierId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var record = await SelectByIdAsync(connection, tx, supplierId, ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// Ordered by display name. `Search` matches the display name, legal name, tax id (separators ignored) and the
    /// first/last name of any contact ignoring case and accents (same folding as the customer search).
    /// </summary>
    public async Task<IReadOnlyList<SupplierRecord>> ListAsync(
        CloudTenantScope scope, SupplierListFilter? filter, CancellationToken ct)
    {
        var search = CustomerSearchTerm.Normalize(filter?.Search);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<SupplierRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT {SelectColumns}
            FROM {FromClause}
            WHERE ($1::uuid IS NULL OR s.city_id = $1)
              AND ($2::uuid IS NULL OR s.category_id = $2)
              AND ($3::boolean IS NULL OR s.is_enabled = $3)
              AND ($4::text IS NULL
                   OR translate(lower(
                          s.display_name || ' ' || coalesce(s.legal_name, '') || ' ' || coalesce(s.tax_id, '')),
                      'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $4 || '%' ESCAPE '\'
                   OR EXISTS (
                      SELECT 1 FROM supplier_contacts sc2
                      WHERE sc2.supplier_id = s.id
                        AND translate(lower(sc2.first_name || ' ' || coalesce(sc2.last_name, '')),
                      'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $4 || '%' ESCAPE '\'))
            ORDER BY s.display_name, s.id
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)filter?.CityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)filter?.CategoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, (object?)filter?.Enabled ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)search ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
            }
        }

        if (results.Count > 0)
        {
            var contacts = await LoadContactsAsync(connection, tx, results.Select(r => r.Id).ToArray(), ct);
            for (var i = 0; i < results.Count; i++)
            {
                if (contacts.TryGetValue(results[i].Id, out var own))
                {
                    results[i] = results[i] with { Contacts = own };
                }
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
}

/// <summary>Organization-owned supplier categories ("rubros", `supplier_categories`); audit entity "supplier_category".</summary>
public sealed class PostgresSupplierCategoryStore : PostgresMasterDataStore
{
    public PostgresSupplierCategoryStore(NpgsqlDataSource dataSource)
        : base(dataSource, "supplier_categories", "supplier_category") { }
}
