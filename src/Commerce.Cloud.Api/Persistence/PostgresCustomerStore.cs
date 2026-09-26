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
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(23));

    private const string SelectColumns =
        """
        id, organization_id, customer_kind, display_name, legal_name, tax_id_type, tax_id,
        tax_condition, phone, email, address_street, address_number, neighborhood, locality,
        province, postal_code, delivery_notes, discount_percentage, payment_terms, notes,
        is_enabled, created_at_utc, created_by_user_id, updated_at_utc
        """;

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

        CustomerRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO customers
                (id, organization_id, customer_kind, display_name, legal_name, tax_id_type, tax_id,
                 tax_condition, phone, email, address_street, address_number, neighborhood, locality,
                 province, postal_code, delivery_notes, discount_percentage, payment_terms, notes,
                 created_by_user_id)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
            RETURNING {SelectColumns}
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

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = Read(reader);
        }

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

        CustomerRecord? existing = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM customers WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(customerId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existing = Read(reader);
            }
        }

        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        CustomerRecord updated;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE customers
            SET display_name = $1, legal_name = $2, tax_id_type = $3, tax_id = $4, tax_condition = $5,
                phone = $6, email = $7, address_street = $8, address_number = $9, neighborhood = $10,
                locality = $11, province = $12, postal_code = $13, delivery_notes = $14,
                discount_percentage = $15, payment_terms = $16, notes = $17, is_enabled = $18,
                updated_at_utc = now()
            WHERE id = $19
            RETURNING {SelectColumns}
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
            cmd.Parameters.AddWithValue(customerId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            updated = Read(reader);
        }

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

        await using var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM customers WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(customerId);

        CustomerRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = Read(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<CustomerRecord>> ListAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CustomerRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {SelectColumns} FROM customers ORDER BY display_name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
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
