using System.Globalization;
using System.Text;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed customer store (commerce-customer-identity design.md
/// "Interfaces / Contracts"), mirroring <see cref="PostgresOrganizationStore"/>'s
/// exact shape: raw <see cref="NpgsqlDataSource"/>, every scoped method opens
/// its own <see cref="NpgsqlTransaction"/>, `set_config` is always the FIRST
/// statement. Create/Update write their audit row in the SAME transaction as
/// the mutation (design.md "Data Flow" — "ONE tx"), so the row and the audit
/// entry commit together or not at all.
/// </summary>
public sealed class PostgresCustomerStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCustomerStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
    }

    private static CustomerRecord Read(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        CustomerKind: Enum.Parse<CustomerKind>(reader.GetString(2)),
        DisplayName: reader.GetString(3),
        LegalName: reader.IsDBNull(4) ? null : reader.GetString(4),
        TaxIdType: Enum.Parse<TaxIdType>(reader.GetString(5)),
        TaxId: reader.IsDBNull(6) ? null : reader.GetString(6),
        TaxCondition: Enum.Parse<TaxCondition>(reader.GetString(7)),
        Phone: reader.IsDBNull(8) ? null : reader.GetString(8),
        Email: reader.IsDBNull(9) ? null : reader.GetString(9),
        AddressStreet: reader.IsDBNull(10) ? null : reader.GetString(10),
        AddressNumber: reader.IsDBNull(11) ? null : reader.GetString(11),
        Neighborhood: reader.IsDBNull(12) ? null : reader.GetString(12),
        Locality: reader.IsDBNull(13) ? null : reader.GetString(13),
        Province: reader.IsDBNull(14) ? null : reader.GetString(14),
        PostalCode: reader.IsDBNull(15) ? null : reader.GetString(15),
        DeliveryNotes: reader.IsDBNull(16) ? null : reader.GetString(16),
        DiscountPercentage: reader.IsDBNull(17) ? null : reader.GetDecimal(17),
        PaymentTerms: reader.IsDBNull(18) ? null : reader.GetString(18),
        Notes: reader.IsDBNull(19) ? null : reader.GetString(19),
        IsEnabled: reader.GetBoolean(20),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(21),
        CreatedByUserId: reader.GetGuid(22),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(23),
        CityId: reader.IsDBNull(24) ? null : reader.GetGuid(24),
        CityName: reader.IsDBNull(25) ? null : reader.GetString(25),
        BusinessTypeId: reader.IsDBNull(26) ? null : reader.GetGuid(26),
        BusinessTypeName: reader.IsDBNull(27) ? null : reader.GetString(27),
        ProvinceId: reader.IsDBNull(28) ? null : reader.GetString(28),
        ProvinceName: reader.IsDBNull(29) ? null : reader.GetString(29));

    // The LEFT JOINs resolve the display names of the optional city (global
    // geography, with its province) and business type (organization catalog;
    // the composite key keeps it inside the row's organization).
    private const string SelectColumns =
        """
        c.id, c.organization_id, c.customer_kind, c.display_name, c.legal_name, c.tax_id_type, c.tax_id,
        c.tax_condition, c.phone, c.email, c.address_street, c.address_number, c.neighborhood, c.locality,
        c.province, c.postal_code, c.delivery_notes, c.discount_percentage, c.payment_terms, c.notes,
        c.is_enabled, c.created_at_utc, c.created_by_user_id, c.updated_at_utc,
        c.city_id, ci.name, c.business_type_id, bt.name, ci.province_id, pr.name
        """;

    private const string FromClause =
        """
        customers c
        LEFT JOIN cities ci ON ci.id = c.city_id
        LEFT JOIN provinces pr ON pr.id = ci.province_id
        LEFT JOIN business_types bt ON bt.organization_id = c.organization_id AND bt.id = c.business_type_id
        """;

    private static async Task<CustomerRecord?> SelectByIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid customerId, CancellationToken ct)
    {
        CustomerRecord? record;
        await using (var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM {FromClause} WHERE c.id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(customerId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            record = await reader.ReadAsync(ct) ? Read(reader) : null;
        }

        if (record is null)
        {
            return null;
        }

        var contacts = await LoadContactsAsync(connection, tx, [customerId], ct);
        return contacts.TryGetValue(customerId, out var own) ? record with { Contacts = own } : record;
    }

    /// <summary>The contacts of the given customers in one query, ordered by `sort_order` then creation.</summary>
    private static async Task<Dictionary<Guid, List<CustomerContactRecord>>> LoadContactsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid[] customerIds, CancellationToken ct)
    {
        var byCustomer = new Dictionary<Guid, List<CustomerContactRecord>>();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT customer_id, id, first_name, last_name, phone, email, role, is_primary, sort_order
            FROM customer_contacts
            WHERE customer_id = ANY($1)
            ORDER BY sort_order, created_at_utc, id
            """, connection, tx);
        cmd.Parameters.AddWithValue(customerIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var customerId = reader.GetGuid(0);
            if (!byCustomer.TryGetValue(customerId, out var list))
            {
                byCustomer[customerId] = list = [];
            }

            list.Add(new CustomerContactRecord(
                reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetBoolean(7), reader.GetInt32(8)));
        }

        return byCustomer;
    }

    /// <summary>
    /// REPLACE-SET of one customer's contacts inside the caller's transaction: contacts whose
    /// id is not sent are deleted, the primary flag is cleared (the partial unique index allows
    /// one primary, so it is re-applied per contact), then every sent contact is upserted by id.
    /// An id that belongs to another customer or organization is refused.
    /// </summary>
    private static async Task ReplaceContactsAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid organizationId, Guid customerId,
        IReadOnlyList<CustomerContactInput> contacts, CancellationToken ct)
    {
        var keptIds = contacts.Where(c => c.Id is not null).Select(c => c.Id!.Value).ToArray();
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM customer_contacts WHERE customer_id = $1 AND NOT (id = ANY($2))", connection, tx))
        {
            cmd.Parameters.AddWithValue(customerId);
            cmd.Parameters.AddWithValue(keptIds);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE customer_contacts SET is_primary = false WHERE customer_id = $1 AND is_primary", connection, tx))
        {
            cmd.Parameters.AddWithValue(customerId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var contact in contacts)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO customer_contacts
                    (id, organization_id, customer_id, first_name, last_name, phone, email, role, is_primary, sort_order)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (id) DO UPDATE
                SET first_name = EXCLUDED.first_name, last_name = EXCLUDED.last_name, phone = EXCLUDED.phone,
                    email = EXCLUDED.email, role = EXCLUDED.role, is_primary = EXCLUDED.is_primary,
                    sort_order = EXCLUDED.sort_order, updated_at_utc = now()
                WHERE customer_contacts.customer_id = EXCLUDED.customer_id
                """, connection, tx);
            cmd.Parameters.AddWithValue(contact.Id ?? Guid.NewGuid());
            cmd.Parameters.AddWithValue(organizationId);
            cmd.Parameters.AddWithValue(customerId);
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
                ex.TableName == "customer_contacts"
                && ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UniqueViolation)
            {
                // The id points at a contact this organization cannot see (row level security) or that is taken.
                throw new CustomerContactRejectedException($"Contact id {contact.Id} cannot be used.");
            }

            if (affected == 0)
            {
                // ON CONFLICT matched a contact of ANOTHER customer of this organization.
                throw new CustomerContactRejectedException($"Contact id {contact.Id} belongs to another customer.");
            }
        }
    }

    /// <summary>
    /// ONE transaction: set_config -> INSERT customers (WITH CHECK pins
    /// organization_id) -> audit row ("customer.created") -> COMMIT. Cross-org
    /// creation is unrepresentable: `organization_id` is never a caller field.
    /// </summary>
    public async Task<CustomerRecord> CreateAsync(
        CloudTenantScope scope, NewCustomer customer, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO customers
                (id, organization_id, customer_kind, display_name, legal_name, tax_id_type, tax_id,
                 tax_condition, phone, email, address_street, address_number, neighborhood, locality,
                 province, postal_code, delivery_notes, discount_percentage, payment_terms, notes,
                 created_by_user_id, city_id, business_type_id)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21,
                 $22, $23)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(customer.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(customer.CustomerKind.ToString());
            cmd.Parameters.AddWithValue(customer.DisplayName);
            cmd.Parameters.AddWithValue((object?)customer.LegalName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(customer.TaxIdType.ToString());
            cmd.Parameters.AddWithValue((object?)customer.TaxId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(customer.TaxCondition.ToString());
            cmd.Parameters.AddWithValue((object?)customer.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.AddressStreet ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.AddressNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.Neighborhood ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.Locality ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.Province ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.PostalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.DeliveryNotes ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.DiscountPercentage ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.PaymentTerms ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(customer.CreatedByUserId);
            cmd.Parameters.AddWithValue((object?)customer.CityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)customer.BusinessTypeId ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (customer.Contacts is { Count: > 0 } contacts)
        {
            await ReplaceContactsAsync(connection, tx, scope.OrganizationId, customer.Id, contacts, ct);
        }

        // Re-read through the joined select so the record carries the city and business type names.
        var record = (await SelectByIdAsync(connection, tx, customer.Id, ct))!;

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "customer", customer.Id, "customer.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// ONE transaction: set_config -> read the CURRENT row (old value, and
    /// the existence/cross-org check) -> UPDATE -> audit row
    /// ("customer.updated", old/new display_name) -> COMMIT. A cross-org
    /// target is invisible under RLS, so this returns null identically to a
    /// nonexistent id (customer-registry spec "Second organization cannot
    /// read or write another org's customers").
    /// </summary>
    public async Task<CustomerRecord?> UpdateAsync(
        CloudTenantScope scope, Guid customerId, UpdateCustomer update, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var existing = await SelectByIdAsync(connection, tx, customerId, ct);

        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // Optional columns: an absent change keeps the stored value, decided IN
        // SQL (CASE on a flag) so a concurrent write to that column between our
        // read and our write is never overwritten with a stale copy (F2). The
        // optimistic token is part of the WHERE for the same reason: the check
        // and the write are one statement, not a read followed by a write.
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE customers
            SET display_name = $1, legal_name = $2, tax_id_type = $3, tax_id = $4, tax_condition = $5,
                phone = $6, email = $7, address_street = $8, address_number = $9, neighborhood = $10,
                locality = $11, province = $12, postal_code = $13, delivery_notes = $14,
                discount_percentage = $15, payment_terms = $16, notes = $17, is_enabled = $18,
                city_id = CASE WHEN $19 THEN $20::uuid ELSE city_id END,
                business_type_id = CASE WHEN $21 THEN $22::uuid ELSE business_type_id END,
                updated_at_utc = now()
            WHERE id = $23 AND ($24::timestamptz IS NULL OR updated_at_utc = $24)
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
            cmd.Parameters.AddWithValue((object?)update.Locality ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.Province ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.PostalCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.DeliveryNotes ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.DiscountPercentage ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.PaymentTerms ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.IsEnabled);
            cmd.Parameters.AddWithValue(update.City is not null);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)update.City?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue(update.BusinessType is not null);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid, (object?)update.BusinessType?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue(customerId);
            cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)update.ExpectedUpdatedAtUtc ?? DBNull.Value);

            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                // The row was visible a moment ago, so the token did not match.
                await tx.RollbackAsync(ct);
                throw new CustomerModifiedException(customerId);
            }
        }

        // Same transaction as the token-checked UPDATE above: a stale token rolls the contacts back too.
        if (update.Contacts is not null)
        {
            await ReplaceContactsAsync(connection, tx, scope.OrganizationId, customerId, update.Contacts, ct);
        }

        var updated = (await SelectByIdAsync(connection, tx, customerId, ct))!;

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "customer", customerId, "customer.updated",
                OldValueJson: $$"""{"displayName":"{{existing.DisplayName}}"}""",
                NewValueJson: $$"""{"displayName":"{{updated.DisplayName}}"}"""),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<CustomerRecord?> FindAsync(CloudTenantScope scope, Guid customerId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var record = await SelectByIdAsync(connection, tx, customerId, ct);

        await tx.CommitAsync(ct);
        return record;
    }

    public Task<IReadOnlyList<CustomerRecord>> ListAsync(CloudTenantScope scope, CancellationToken ct) =>
        ListAsync(scope, null, ct);

    /// <summary>
    /// Ordered by display name. `Search` matches the name, legal name, tax id
    /// and the first/last name of any contact ignoring case and accents (a plain `translate`/`lower`
    /// fold, so no database extension is needed); LIKE wildcards in the term
    /// are escaped, so `%` and `_` only match themselves.
    /// </summary>
    public async Task<IReadOnlyList<CustomerRecord>> ListAsync(
        CloudTenantScope scope, CustomerListFilter? filter, CancellationToken ct)
    {
        var search = CustomerSearchTerm.Normalize(filter?.Search);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CustomerRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT {SelectColumns}
            FROM {FromClause}
            WHERE ($1::uuid IS NULL OR c.city_id = $1)
              AND ($2::uuid IS NULL OR c.business_type_id = $2)
              AND ($3::text IS NULL
                   OR translate(lower(
                          c.display_name || ' ' || coalesce(c.legal_name, '') || ' ' || coalesce(c.tax_id, '')),
                      'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $3 || '%' ESCAPE '\'
                   OR EXISTS (
                      SELECT 1 FROM customer_contacts cc
                      WHERE cc.customer_id = c.id
                        AND translate(lower(cc.first_name || ' ' || coalesce(cc.last_name, '')),
                      'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc') LIKE '%' || $3 || '%' ESCAPE '\'))
            ORDER BY c.display_name, c.id
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue((object?)filter?.CityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)filter?.BusinessTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)search ?? DBNull.Value);
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

    /// <summary>
    /// Minimum viable pull projection for `GET /device/customers/sync` (Unit
    /// 6): enabled customers with `updated_at_utc > since`, org-scoped.
    /// </summary>
    public async Task<IReadOnlyList<CustomerReplicaRow>> ListChangedSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CustomerReplicaRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id, display_name, customer_kind, tax_id, phone, locality, updated_at_utc
            FROM customers
            WHERE is_enabled AND updated_at_utc > $1
            ORDER BY updated_at_utc
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new CustomerReplicaRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// The other half of the sync projection (design.md "BranchNode
    /// cloud->local customer replication"): ids of customers DISABLED after
    /// the cursor, so a revocation propagates to the replica on the very
    /// next pull instead of only appearing implicitly by absence.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListDisabledSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT id FROM customers
            WHERE NOT is_enabled AND updated_at_utc > $1
            ORDER BY updated_at_utc
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
}

/// <summary>
/// Folds a free-text search term the same way <see cref="PostgresCustomerStore.ListAsync"/>
/// folds the stored text: lowercase, accents removed, LIKE wildcards escaped.
/// Separators typed inside a tax id ("30-12.345") are dropped when the term is
/// nothing but digits and separators, since tax ids are stored digits only.
/// </summary>
internal static class CustomerSearchTerm
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var term = raw.Trim();
        if (term.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or ' '))
        {
            term = new string(term.Where(char.IsAsciiDigit).ToArray());
            if (term.Length == 0)
            {
                return null;
            }
        }

        return FoldForLike(term);
    }

    /// <summary>
    /// Lowercase, accents removed, LIKE wildcards escaped (no tax-id handling):
    /// the term of free-text lookups such as the city picker. Null when blank.
    /// </summary>
    public static string? FoldForLike(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var decomposed = raw.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(ch);
            }
        }

        return folded.ToString().Normalize(NormalizationForm.FormC)
            .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
