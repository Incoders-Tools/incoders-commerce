using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Vehicles;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>A vehicle as listed and edited; <see cref="Label"/> is how it reads on a delivery run.</summary>
public sealed record VehicleRecord(
    Guid Id,
    string Plate,
    string? Brand,
    string? Model,
    int? Year,
    string? Notes,
    bool IsActive,
    string Label,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>A vehicle as written: the plate already normalized (<see cref="VehicleRules.NormalizePlate"/>).</summary>
public sealed record VehicleInput(string Plate, string? Brand, string? Model, int? Year, string? Notes, bool IsActive);

public enum VehicleWriteOutcome
{
    Done,
    NotFound,
    /// <summary>Another vehicle of the organization has that plate.</summary>
    PlateInUse,
}

/// <summary>The organization's vehicles (0057, vehicles.md): a lookup table, by plate; audit entity "vehicle".</summary>
public sealed class PostgresVehicleStore(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "id, plate, brand, model, model_year, notes, is_active, created_at_utc, updated_at_utc";

    public async Task<IReadOnlyList<VehicleRecord>> ListAsync(CloudTenantScope scope, bool includeInactive, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM vehicles WHERE $1 OR is_active ORDER BY plate", connection, tx);
        cmd.Parameters.AddWithValue(includeInactive);
        var vehicles = new List<VehicleRecord>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                vehicles.Add(Read(reader));
            }
        }

        await tx.CommitAsync(ct);
        return vehicles;
    }

    public Task<(VehicleWriteOutcome Outcome, VehicleRecord? Vehicle)> CreateAsync(
        CloudTenantScope scope, Guid actorId, VehicleInput input, CancellationToken ct) =>
        WriteAsync(scope, actorId, null, input, ct);

    public Task<(VehicleWriteOutcome Outcome, VehicleRecord? Vehicle)> UpdateAsync(
        CloudTenantScope scope, Guid actorId, Guid id, VehicleInput input, CancellationToken ct) =>
        WriteAsync(scope, actorId, id, input, ct);

    private async Task<(VehicleWriteOutcome Outcome, VehicleRecord? Vehicle)> WriteAsync(
        CloudTenantScope scope, Guid actorId, Guid? id, VehicleInput input, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, null, ct);
        await using var cmd = new NpgsqlCommand(
            id is null
                ? $"""
                   INSERT INTO vehicles (organization_id, id, plate, brand, model, model_year, notes, is_active, created_by_user_id)
                   VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                   RETURNING {Columns}
                   """
                : $"""
                   UPDATE vehicles SET plate = $3, brand = $4, model = $5, model_year = $6, notes = $7, is_active = $8, updated_at_utc = now()
                   WHERE organization_id = $1 AND id = $2
                   RETURNING {Columns}
                   """,
            connection, tx);
        cmd.Parameters.AddWithValue(scope.OrganizationId);
        cmd.Parameters.AddWithValue(id ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue(input.Plate);
        cmd.Parameters.AddWithValue(Nullable(input.Brand));
        cmd.Parameters.AddWithValue(Nullable(input.Model));
        cmd.Parameters.AddWithValue(input.Year is { } year ? year : DBNull.Value);
        cmd.Parameters.AddWithValue(Nullable(input.Notes));
        cmd.Parameters.AddWithValue(input.IsActive);
        if (id is null)
        {
            cmd.Parameters.AddWithValue(actorId);
        }

        VehicleRecord? vehicle = null;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                vehicle = Read(reader);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "vehicles_plate_uk")
        {
            await tx.RollbackAsync(ct);
            return (VehicleWriteOutcome.PlateInUse, null);
        }

        if (vehicle is null)
        {
            await tx.RollbackAsync(ct);
            return (VehicleWriteOutcome.NotFound, null);
        }

        await PostgresEmployeeStore.AuditAsync(connection, tx, scope.OrganizationId, actorId, vehicle.Id,
            id is null ? "vehicle.created" : "vehicle.updated",
            new { vehicle.Plate, vehicle.Brand, vehicle.Model, vehicle.Year, vehicle.Notes, vehicle.IsActive }, ct, entityType: "vehicle");
        await tx.CommitAsync(ct);
        return (VehicleWriteOutcome.Done, vehicle);
    }

    private static VehicleRecord Read(NpgsqlDataReader reader)
    {
        var plate = reader.GetString(1);
        var brand = reader.IsDBNull(2) ? null : reader.GetString(2);
        var model = reader.IsDBNull(3) ? null : reader.GetString(3);
        return new VehicleRecord(
            reader.GetGuid(0), plate, brand, model, reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6), VehicleRules.Label(plate, brand, model), reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static object Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
