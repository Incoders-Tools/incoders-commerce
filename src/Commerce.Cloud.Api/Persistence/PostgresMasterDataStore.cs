using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store for the organization-owned customer catalog (business
/// types; cities became global in 0028), shaped like <see cref="PostgresCategoryStore"/>: every
/// method opens its own transaction, `set_config` is the first statement and
/// mutations write their audit row in the same transaction. There is no
/// delete: an entry is disabled through <c>IsActive</c> so customers keep their
/// reference. A name or key clash surfaces as a <see cref="PostgresException"/>
/// (23505) that the endpoint translates into a 409. The table and audit names
/// are fixed constants of the two concrete stores, never request data.
/// </summary>
public abstract class PostgresMasterDataStore
{
    private const string Columns = "id, organization_id, name, key, sort_order, is_active, created_at_utc, updated_at_utc";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _table;
    private readonly string _entity;

    protected PostgresMasterDataStore(NpgsqlDataSource dataSource, string table, string entity)
    {
        _dataSource = dataSource;
        _table = table;
        _entity = entity;
    }

    private static MasterDataRecord Read(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        OrganizationId: reader.GetGuid(1),
        Name: reader.GetString(2),
        Key: reader.GetString(3),
        SortOrder: reader.GetInt32(4),
        IsActive: reader.GetBoolean(5),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(6),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(7));

    public async Task<IReadOnlyList<MasterDataRecord>> ListAsync(
        CloudTenantScope scope, bool includeInactive, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        var results = new List<MasterDataRecord>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {_table} WHERE ($1 OR is_active) ORDER BY sort_order, lower(name), id", connection, tx))
        {
            cmd.Parameters.AddWithValue(includeInactive);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(Read(reader));
            }
        }

        await tx.CommitAsync(ct);
        return results;
    }

    public async Task<MasterDataRecord> CreateAsync(
        CloudTenantScope scope, NewMasterDataEntry entry, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        MasterDataRecord record;
        await using (var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} (id, organization_id, name, key, sort_order, is_active)
            VALUES ($1, $2, $3, $4, $5, $6)
            RETURNING {Columns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(entry.Id);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(entry.Name);
            cmd.Parameters.AddWithValue(entry.Key);
            cmd.Parameters.AddWithValue(entry.SortOrder);
            cmd.Parameters.AddWithValue(entry.IsActive);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            record = Read(reader);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, _entity, entry.Id, $"{_entity}.created",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>Null when the id is not visible in the scope's organization.</summary>
    public async Task<MasterDataRecord?> UpdateAsync(
        CloudTenantScope scope, Guid id, UpdateMasterDataEntry update, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        MasterDataRecord? updated = null;
        await using (var cmd = new NpgsqlCommand(
            $"""
            UPDATE {_table}
            SET name = $1, key = COALESCE($2::text, key), sort_order = COALESCE($3::integer, sort_order),
                is_active = COALESCE($4::boolean, is_active), updated_at_utc = now()
            WHERE id = $5
            RETURNING {Columns}
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(update.Name);
            cmd.Parameters.AddWithValue((object?)update.Key ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.SortOrder ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)update.IsActive ?? DBNull.Value);
            cmd.Parameters.AddWithValue(id);
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
                actorKind, actorId, scope.OrganizationId, _entity, id, $"{_entity}.updated",
                OldValueJson: null, NewValueJson: null),
            ct);

        await tx.CommitAsync(ct);
        return updated;
    }
}

/// <summary>Organization-owned business types (`business_types`); audit entity "business_type".</summary>
public sealed class PostgresBusinessTypeStore : PostgresMasterDataStore
{
    public PostgresBusinessTypeStore(NpgsqlDataSource dataSource) : base(dataSource, "business_types", "business_type") { }
}
