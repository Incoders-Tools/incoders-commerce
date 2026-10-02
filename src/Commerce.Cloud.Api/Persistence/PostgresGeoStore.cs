using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Npgsql-backed store for the core geography (`countries`, `provinces`,
/// `cities`; migration 0028). These tables are global reference data, not tenant
/// data: reads need no tenant scope and every organization sees the same rows.
/// Writes (system-administrator endpoints only) run in a transaction that also
/// writes the audit row, scoped to the CALLER's own organization because the
/// audit log is tenant-scoped. A name clash or an unknown province surfaces as a
/// <see cref="PostgresException"/> (23505 / 23503) for the endpoint to translate.
/// </summary>
public sealed class PostgresGeoStore
{
    private const string CityColumns =
        """
        c.id, c.indec_id, c.name, c.province_id, p.name, p.country_code, c.department_name, c.is_active,
        c.created_at_utc, c.updated_at_utc
        """;

    private const string CityFrom = "cities c JOIN provinces p ON p.id = c.province_id";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresGeoStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static CityRecord ReadCity(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        IndecId: reader.IsDBNull(1) ? null : reader.GetString(1),
        Name: reader.GetString(2),
        ProvinceId: reader.GetString(3),
        ProvinceName: reader.GetString(4),
        CountryCode: reader.GetString(5),
        DepartmentName: reader.IsDBNull(6) ? null : reader.GetString(6),
        IsActive: reader.GetBoolean(7),
        CreatedAtUtc: reader.GetFieldValue<DateTimeOffset>(8),
        UpdatedAtUtc: reader.GetFieldValue<DateTimeOffset>(9));

    public async Task<IReadOnlyList<ProvinceRecord>> ListProvincesAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.id, p.iso_code, p.name, p.country_code, co.name
            FROM provinces p JOIN countries co ON co.code = p.country_code
            ORDER BY translate(lower(p.name), 'áéíóúüñàèìòùâêîôûäëïöç', 'aeiouunaeiouaeiouaeioc'), p.id
            """, connection);
        var results = new List<ProvinceRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProvinceRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return results;
    }

    /// <summary>
    /// Folded-name search (substring), prefix matches first, then shorter names,
    /// then alphabetical; without a search term, alphabetical within the filters.
    /// </summary>
    public async Task<IReadOnlyList<CityRecord>> SearchCitiesAsync(CitySearch search, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            SELECT {CityColumns}
            FROM {CityFrom}
            WHERE ($1::text IS NULL OR c.province_id = $1)
              AND ($2 OR c.is_active)
              AND ($3::text IS NULL OR c.search_key LIKE '%' || $3 || '%' ESCAPE '\')
            ORDER BY CASE WHEN $3::text IS NOT NULL AND c.search_key LIKE $3 || '%' ESCAPE '\' THEN 0 ELSE 1 END,
                     CASE WHEN $3::text IS NULL THEN 0 ELSE length(c.search_key) END,
                     c.search_key, coalesce(c.department_name, ''), c.id
            LIMIT $4 OFFSET $5
            """, connection);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)search.ProvinceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(search.IncludeInactive);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)search.FoldedSearch ?? DBNull.Value);
        cmd.Parameters.AddWithValue(search.Limit);
        cmd.Parameters.AddWithValue(search.Offset);

        var results = new List<CityRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadCity(reader));
        }

        return results;
    }

    public async Task<CityRecord?> FindCityAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await SelectCityAsync(connection, null, id, ct);
    }

    private static async Task<CityRecord?> SelectCityAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {CityColumns} FROM {CityFrom} WHERE c.id = $1", connection, tx);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCity(reader) : null;
    }

    public async Task<CityRecord> CreateCityAsync(CloudTenantScope scope, NewCity city, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.IdentityScope, ct);

        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO cities (id, name, province_id, department_name, is_active) VALUES ($1, $2, $3, $4, $5)", connection, tx))
        {
            cmd.Parameters.AddWithValue(city.Id);
            cmd.Parameters.AddWithValue(city.Name);
            cmd.Parameters.AddWithValue(city.ProvinceId);
            cmd.Parameters.AddWithValue((object?)city.DepartmentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(city.IsActive);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var record = (await SelectCityAsync(connection, tx, city.Id, ct))!;
        await AuditAsync(connection, tx, scope, actorId, city.Id, "city.created", ct);
        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>Null when the city does not exist.</summary>
    public async Task<CityRecord?> UpdateCityAsync(CloudTenantScope scope, Guid id, UpdateCity update, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.IdentityScope, ct);

        int affected;
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE cities
            SET name = $1,
                province_id = COALESCE($2::text, province_id),
                department_name = CASE WHEN $3::text IS NULL THEN department_name ELSE NULLIF(btrim($3), '') END,
                is_active = COALESCE($4::boolean, is_active),
                updated_at_utc = now()
            WHERE id = $5
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(update.Name);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)update.ProvinceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)update.DepartmentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Boolean, (object?)update.IsActive ?? DBNull.Value);
            cmd.Parameters.AddWithValue(id);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var record = (await SelectCityAsync(connection, tx, id, ct))!;
        await AuditAsync(connection, tx, scope, actorId, id, "city.updated", ct);
        await tx.CommitAsync(ct);
        return record;
    }

    private static Task AuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, Guid actorId, Guid cityId, string action, CancellationToken ct) =>
        AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                "org-user", actorId, scope.IdentityScope.OrganizationId, "city", cityId, action, OldValueJson: null, NewValueJson: null),
            ct);
}
