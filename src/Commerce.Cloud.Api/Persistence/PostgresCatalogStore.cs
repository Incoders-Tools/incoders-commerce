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
        "id, organization_id, branch_id, name, category_id, default_unit_id, created_at_utc, created_by_user_id, updated_at_utc, " +
        "is_active, deactivated_at_utc";

    private static ProductRecord ReadProduct(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        BranchId: reader.GetGuid(2),
        Name: reader.GetString(3),
        CategoryId: reader.GetGuid(4),
        DefaultUnitId: reader.GetGuid(5),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(6),
        CreatedByUserId: reader.GetGuid(7),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(8),
        IsActive: reader.GetBoolean(9),
        DeactivatedAtUtc: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));

    /// <summary>
    /// Requires <paramref name="scope"/> to already carry a selected
    /// branch — every caller MUST have run
    /// <see cref="Tenancy.BranchSelectionRequirement.Enforce"/> first
    /// (B7 U4). The branch is stamped from the scope, never from any
    /// caller-submitted field.
    /// </summary>
    public async Task<ProductRecord> CreateProductAsync(
        CloudTenantScope scope, NewProduct product, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = scope.BranchId
            ?? throw new InvalidOperationException("CreateProductAsync requires a selected branch.");

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        ProductRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            RETURNING {ProductColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(product.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(branchId);
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

    /// <summary>
    /// Soft deletion (0036): flips `is_active`, stamps `deactivated_at_utc` and BUMPS `updated_at_utc`, which is what the
    /// POS replica cursor follows, so the branches learn about the removal (or the return). One transaction with its audit
    /// row ("product.deactivated" / "product.reactivated"). Idempotent: repeating the current state changes and audits
    /// nothing. Null for an unknown (or cross-tenant, invisible under RLS) id. History is never touched.
    /// </summary>
    public async Task<ProductRecord?> SetProductActiveAsync(
        CloudTenantScope scope, Guid productId, bool active, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        ProductRecord? existing = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {ProductColumns} FROM products WHERE id = $1 FOR UPDATE", connection, tx))
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

        if (existing.IsActive == active)
        {
            await tx.CommitAsync(ct);
            return existing;
        }

        ProductRecord updated;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE products
            SET is_active = $1, deactivated_at_utc = CASE WHEN $1 THEN NULL ELSE now() END, updated_at_utc = now()
            WHERE id = $2
            RETURNING {ProductColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(active);
            cmd.Parameters.AddWithValue(productId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            updated = ReadProduct(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "product", productId,
                active ? "product.reactivated" : "product.deactivated",
                OldValueJson: $$"""{"isActive":{{(existing.IsActive ? "true" : "false")}}}""",
                NewValueJson: $$"""{"isActive":{{(updated.IsActive ? "true" : "false")}}}"""),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    /// <summary>Inactive products (soft deleted, 0036) are left out unless <paramref name="includeInactive"/>.</summary>
    public async Task<IReadOnlyList<ProductRecord>> ListProductsAsync(
        CloudTenantScope scope, CancellationToken ct, bool includeInactive = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<ProductRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {ProductColumns} FROM products WHERE ($1 OR is_active) ORDER BY name", connection, tx))
        {
            cmd.Parameters.AddWithValue(includeInactive);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
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
        "id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, identification_code, " +
        "created_at_utc, created_by_user_id, updated_at_utc";

    private static PresentationRecord ReadPresentation(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        BranchId: reader.GetGuid(2),
        ProductId: reader.GetGuid(3),
        Name: reader.GetString(4),
        QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(5)),
        UnitId: reader.GetGuid(6),
        IdentificationCode: reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(8),
        CreatedByUserId: reader.GetGuid(9),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(10));

    /// <summary>
    /// A duplicate `identification_code` within the same BRANCH (B7 U4;
    /// was within the same organization) is rejected by
    /// `presentations_org_branch_code_uk` — surfaced here as a
    /// <see cref="PostgresException"/> with SqlState `23505` for the caller
    /// (endpoint) to translate into a 409, never swallowed. Requires
    /// <paramref name="scope"/> to already carry a selected branch — see
    /// <see cref="CreateProductAsync"/>.
    /// </summary>
    public async Task<PresentationRecord> CreatePresentationAsync(
        CloudTenantScope scope, NewPresentation presentation, string actorKind, Guid actorId, CancellationToken ct)
    {
        var branchId = scope.BranchId
            ?? throw new InvalidOperationException("CreatePresentationAsync requires a selected branch.");

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        PresentationRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO presentations
                (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, identification_code, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            RETURNING {PresentationColumns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(presentation.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(branchId);
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

    /// <summary>Presentations of inactive (soft deleted, 0036) products are left out unless <paramref name="includeInactive"/>.</summary>
    public async Task<IReadOnlyList<PresentationRecord>> ListPresentationsAsync(
        CloudTenantScope scope, CancellationToken ct, bool includeInactive = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<PresentationRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            SELECT {PresentationColumns} FROM presentations
            WHERE ($1 OR product_id IN (SELECT id FROM products WHERE is_active))
            ORDER BY name
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(includeInactive);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadPresentation(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// The device sync projection shared by both sync reads: a presentation,
    /// its product, and (LEFT JOIN, so a product with no category still syncs)
    /// the product's category (catalog-categories spec).
    /// </summary>
    private const string ChangeProjection =
        """
        SELECT p.id, pr.id, pr.name, p.name, p.identification_code, p.quantity_behavior, p.unit_id, p.updated_at_utc,
               cat.id, cat.name, cat.icon_key
        FROM presentations p
        JOIN products pr ON pr.id = p.product_id
        LEFT JOIN categories cat ON cat.id = pr.category_id
        """;

    private static CatalogChangeRow ReadChangeRow(NpgsqlDataReader reader) => new(
        PresentationId: reader.GetGuid(0),
        ProductId: reader.GetGuid(1),
        ProductName: reader.GetString(2),
        PresentationName: reader.GetString(3),
        IdentificationCode: reader.IsDBNull(4) ? null : reader.GetString(4),
        QuantityBehavior: Enum.Parse<QuantityBehavior>(reader.GetString(5)),
        UnitId: reader.GetGuid(6),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7),
        CategoryId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
        CategoryName: reader.IsDBNull(9) ? null : reader.GetString(9),
        CategoryIconKey: reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>
    /// Minimum viable pull projection for the future `GET
    /// /device/catalog/sync` (Work Unit 6): presentations (joined to their
    /// product and category) whose presentation, product OR category changed
    /// after `since`, org-scoped. Before catalog-categories the cursor followed
    /// only `presentations.updated_at_utc`, so a product-only edit (name,
    /// category) was never re-sent to devices. `products_org_updated` and
    /// `categories_org_updated` (organization, updated_at_utc) already index the
    /// other two timestamps; a catalog is small enough that the OR does not
    /// need a dedicated index.
    /// </summary>
    public async Task<IReadOnlyList<CatalogChangeRow>> ListChangedSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var results = new List<CatalogChangeRow>();
        await using (var cmd = new NpgsqlCommand(
            $"""
            {ChangeProjection}
            WHERE pr.is_active AND (p.updated_at_utc > $1 OR pr.updated_at_utc > $1 OR cat.updated_at_utc > $1)
            ORDER BY GREATEST(p.updated_at_utc, pr.updated_at_utc, cat.updated_at_utc)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadChangeRow(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    /// <summary>
    /// Presentations of products soft deleted (0036) since the cursor, for the replica's `removedPresentationIds`: the
    /// deactivation bumps `products.updated_at_utc`, the same column the changed-since read follows. A product that comes
    /// back is no longer inactive, so it leaves this list and re-enters <see cref="ListChangedSinceAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListDeactivatedSinceAsync(
        CloudTenantScope scope, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var ids = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id FROM presentations p
            JOIN products pr ON pr.id = p.product_id
            WHERE NOT pr.is_active AND pr.updated_at_utc > $1
            ORDER BY p.id
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(since);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await tx.CommitAsync(ct);
        return ids;
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
            $"""
            {ChangeProjection}
            WHERE pr.is_active AND p.id = ANY($1)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(presentationIds.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadChangeRow(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }
}
