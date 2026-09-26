using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed catalog store (commerce-pricing-engine design.md
/// "Verified deviation" / "Work Unit 1"), mirroring
/// <see cref="PostgresCustomerStore"/>'s exact shape: raw
/// <see cref="NpgsqlDataSource"/>, every scoped method opens its own
/// <see cref="NpgsqlTransaction"/>, `set_config` is always the FIRST
/// statement, Create writes its audit row in the SAME transaction as the
/// mutation. Replaces the previous request-body construction in
/// `CatalogEndpoints` — there is now something real to attach an
/// identification code or a price entry to.
/// </summary>
public sealed class PostgresCatalogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCatalogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
    }

    // --- Products -----------------------------------------------------

    private const string ProductColumns =
        "id, organization_id, name, category_id, default_unit_id, created_at_utc, created_by_user_id, updated_at_utc";

    private static ProductRecord ReadProduct(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        Name: reader.GetString(2),
        CategoryId: reader.GetGuid(3),
        DefaultUnitId: reader.GetGuid(4),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(5),
        CreatedByUserId: reader.GetGuid(6),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7));

    public async Task<ProductRecord> CreateProductAsync(
        CloudTenantScope scope, NewProduct product, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        ProductRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO products (id, organization_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            RETURNING {ProductColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(product.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(product.Name);
            cmd.Parameters.AddWithValue(product.CategoryId);
            cmd.Parameters.AddWithValue(product.DefaultUnitId);
            cmd.Parameters.AddWithValue(product.CreatedByUserId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = ReadProduct(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "product", product.Id, "product.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// ONE transaction: set_config -> read the CURRENT row (old value, and
    /// the existence/cross-org check) -> UPDATE -> audit row
    /// ("product.updated") -> COMMIT. A cross-org target is invisible under
    /// RLS, so this returns null identically to a nonexistent id.
    /// </summary>
    public async Task<ProductRecord?> UpdateProductAsync(
        CloudTenantScope scope, Guid productId, UpdateProduct update, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        ProductRecord? existing = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {ProductColumns} FROM products WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(productId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existing = ReadProduct(reader);
            }
        }

        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        ProductRecord updated;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE products
            SET name = $1, category_id = $2, default_unit_id = $3, updated_at_utc = now()
            WHERE id = $4
            RETURNING {ProductColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(update.Name);
            cmd.Parameters.AddWithValue(update.CategoryId);
            cmd.Parameters.AddWithValue(update.DefaultUnitId);
            cmd.Parameters.AddWithValue(productId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            updated = ReadProduct(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "product", productId, "product.updated",
                OldValueJson: $$"""{"name":"{{existing.Name}}"}""",
                NewValueJson: $$"""{"name":"{{updated.Name}}"}"""),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<ProductRecord?> FindProductAsync(CloudTenantScope scope, Guid productId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {ProductColumns} FROM products WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(productId);

        ProductRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadProduct(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<ProductRecord>> ListProductsAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<ProductRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ProductColumns} FROM products ORDER BY name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadProduct(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    // --- Presentations --------------------------------------------------

    private const string PresentationColumns =
        "id, organization_id, product_id, name, quantity_behavior, unit_id, identification_code, " +
        "created_at_utc, created_by_user_id, updated_at_utc";

    private static PresentationRecord ReadPresentation(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        ProductId: reader.GetGuid(2),
        Name: reader.GetString(3),
        QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(4)),
        UnitId: reader.GetGuid(5),
        IdentificationCode: reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7),
        CreatedByUserId: reader.GetGuid(8),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(9));

    /// <summary>
    /// A duplicate `identification_code` within the same organization is
    /// rejected by `presentations_org_code_uk` — surfaced here as a
    /// <see cref="PostgresException"/> with SqlState `23505` for the caller
    /// (endpoint) to translate into a 409, never swallowed.
    /// </summary>
    public async Task<PresentationRecord> CreatePresentationAsync(
        CloudTenantScope scope, NewPresentation presentation, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        PresentationRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO presentations
                (id, organization_id, product_id, name, quantity_behavior, unit_id, identification_code, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING {PresentationColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(presentation.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(presentation.ProductId);
            cmd.Parameters.AddWithValue(presentation.Name);
            cmd.Parameters.AddWithValue(presentation.QuantityBehavior.ToString());
            cmd.Parameters.AddWithValue(presentation.UnitId);
            cmd.Parameters.AddWithValue((object?)presentation.IdentificationCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(presentation.CreatedByUserId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = ReadPresentation(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "presentation", presentation.Id, "presentation.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<PresentationRecord?> UpdatePresentationAsync(
        CloudTenantScope scope, Guid presentationId, UpdatePresentation update, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        PresentationRecord? existing = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {PresentationColumns} FROM presentations WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(presentationId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existing = ReadPresentation(reader);
            }
        }

        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        PresentationRecord updated;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE presentations
            SET name = $1, quantity_behavior = $2, unit_id = $3, identification_code = $4, updated_at_utc = now()
            WHERE id = $5
            RETURNING {PresentationColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(update.Name);
            cmd.Parameters.AddWithValue(update.QuantityBehavior.ToString());
            cmd.Parameters.AddWithValue(update.UnitId);
            cmd.Parameters.AddWithValue((object?)update.IdentificationCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue(presentationId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            updated = ReadPresentation(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "presentation", presentationId, "presentation.updated",
                OldValueJson: $$"""{"identificationCode":"{{existing.IdentificationCode}}"}""",
                NewValueJson: $$"""{"identificationCode":"{{updated.IdentificationCode}}"}"""),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<PresentationRecord?> FindPresentationAsync(CloudTenantScope scope, Guid presentationId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand($"SELECT {PresentationColumns} FROM presentations WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(presentationId);

        PresentationRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadPresentation(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// Org-scoped lookup by barcode/SKU (design.md "Identification code
    /// placement and uniqueness"): `WHERE organization_id = … AND
    /// identification_code = $1`. Backs both the future POS scan resolution
    /// and the Excel import row matcher.
    /// </summary>
    public async Task<PresentationRecord?> FindByIdentificationCodeAsync(
        CloudTenantScope scope, string identificationCode, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            $"SELECT {PresentationColumns} FROM presentations WHERE identification_code = $1", connection, tx);
        cmd.Parameters.AddWithValue(identificationCode);

        PresentationRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = ReadPresentation(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<PresentationRecord>> ListPresentationsAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<PresentationRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {PresentationColumns} FROM presentations ORDER BY name", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadPresentation(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Minimum viable pull projection for the future `GET
    /// /device/catalog/sync` (Work Unit 6): presentations (joined to their
    /// product) with `updated_at_utc > since`, org-scoped.
    /// </summary>
    public async Task<IReadOnlyList<CatalogChangeRow>> ListChangedSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CatalogChangeRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id, pr.id, pr.name, p.name, p.identification_code, p.quantity_behavior, p.unit_id, p.updated_at_utc
            FROM presentations p
            JOIN products pr ON pr.id = p.product_id
            WHERE p.updated_at_utc > $1
            ORDER BY p.updated_at_utc
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new CatalogChangeRow(
                    PresentationId: reader.GetGuid(0),
                    ProductId: reader.GetGuid(1),
                    ProductName: reader.GetString(2),
                    PresentationName: reader.GetString(3),
                    IdentificationCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                    QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(5)),
                    UnitId: reader.GetGuid(6),
                    UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Batch lookup by id (commerce-pricing-engine design.md "BranchNode
    /// replication"): fetches the full catalog projection for presentation
    /// ids whose PRICE changed since the cursor but whose catalog row did
    /// not, so <c>GET /device/catalog/sync</c> can assemble one combined row
    /// per presentation regardless of which half changed.
    /// </summary>
    public async Task<IReadOnlyList<CatalogChangeRow>> ListByIdsAsync(
        CloudTenantScope scope, IReadOnlyList<Guid> presentationIds, CancellationToken ct)
    {
        if (presentationIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CatalogChangeRow>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id, pr.id, pr.name, p.name, p.identification_code, p.quantity_behavior, p.unit_id, p.updated_at_utc
            FROM presentations p
            JOIN products pr ON pr.id = p.product_id
            WHERE p.id = ANY($1)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(presentationIds.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new CatalogChangeRow(
                    PresentationId: reader.GetGuid(0),
                    ProductId: reader.GetGuid(1),
                    ProductName: reader.GetString(2),
                    PresentationName: reader.GetString(3),
                    IdentificationCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                    QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(5)),
                    UnitId: reader.GetGuid(6),
                    UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7)));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
}
