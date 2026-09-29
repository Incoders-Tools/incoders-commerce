using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store for organization-owned categories (catalog-categories
/// spec), shaped like <see cref="PostgresCatalogStore"/>: every method opens
/// its own transaction, `set_config` is the first statement, and mutations
/// write their audit row in the same transaction. Categories are shared by
/// every branch, so no branch is required. A unique-name clash surfaces as a
/// <see cref="PostgresException"/> (23505) and deleting a category products
/// still use as one (23503) — the endpoint translates both into 409s.
/// </summary>
public sealed class PostgresCategoryStore
{
    private const string Columns = "id, organization_id, name, icon_key, created_at_utc, updated_at_utc";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresCategoryStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static CategoryRecord Read(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        Name: reader.GetString(2),
        IconKey: reader.GetString(3),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(4),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(5));

    public async Task<IReadOnlyList<CategoryRecord>> ListAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<CategoryRecord>();
        await using (var cmd = new NpgsqlCommand($"SELECT {Columns} FROM categories ORDER BY lower(name), id", connection, tx))
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

    public async Task<CategoryRecord?> FindAsync(CloudTenantScope scope, Guid categoryId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        CategoryRecord? record = null;
        await using (var cmd = new NpgsqlCommand($"SELECT {Columns} FROM categories WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(categoryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                record = Read(reader);
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<CategoryRecord> CreateAsync(
        CloudTenantScope scope, NewCategory category, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        CategoryRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO categories (id, organization_id, name, icon_key)
            VALUES ($1, $2, $3, $4)
            RETURNING {Columns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(category.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(category.Name);
            cmd.Parameters.AddWithValue(category.IconKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = Read(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "category", category.Id, "category.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>Renames and/or changes the icon. Null when the id is not visible in the scope's organization.</summary>
    public async Task<CategoryRecord?> UpdateAsync(
        CloudTenantScope scope, Guid categoryId, string name, string iconKey, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        CategoryRecord? updated = null;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE categories SET name = $1, icon_key = $2, updated_at_utc = now()
            WHERE id = $3
            RETURNING {Columns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(name);
            cmd.Parameters.AddWithValue(iconKey);
            cmd.Parameters.AddWithValue(categoryId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                updated = Read(reader);
            }
        }

        if (updated is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "category", categoryId, "category.updated",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }

    /// <summary>
    /// False when the id is not visible in the scope's organization. Throws a
    /// <see cref="PostgresException"/> with SqlState 23503 while any product
    /// (in any branch) still references the category.
    /// </summary>
    public async Task<bool> DeleteAsync(
        CloudTenantScope scope, Guid categoryId, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        int affected;
        await using (var cmd = new NpgsqlCommand("DELETE FROM categories WHERE id = $1", connection, tx))
        {
            cmd.Parameters.AddWithValue(categoryId);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "category", categoryId, "category.deleted",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The id of the organization's "Sin categoría" category, creating it when
    /// it does not exist yet. Concurrency-safe through the unique name index.
    /// </summary>
    public async Task<Guid> EnsureDefaultAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO categories (id, organization_id, name, icon_key)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT DO NOTHING
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(scope.OrganizationId);
            insert.Parameters.AddWithValue(DefaultCategory.Name);
            insert.Parameters.AddWithValue(DefaultCategory.IconKey);
            await insert.ExecuteNonQueryAsync(ct);
        }

        Guid id;
        await using (var select = new NpgsqlCommand(
            "SELECT id FROM categories WHERE lower(btrim(name)) = lower($1)", connection, tx))
        {
            select.Parameters.AddWithValue(DefaultCategory.Name);
            id = (Guid)(await select.ExecuteScalarAsync(ct))!;
        }

        await tx.CommitAsync(ct);
        return id;
    }
}
