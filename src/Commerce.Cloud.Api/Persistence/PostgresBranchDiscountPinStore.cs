using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>What anyone may learn about a branch discount PIN: whether it is set, and when it last changed.</summary>
public sealed record BranchDiscountPinStatus(bool IsSet, long? Version, DateTimeOffset? ChangedAtUtc);

/// <summary>The verifier a paired terminal caches; every byte field is base64. Never carries the PIN.</summary>
public sealed record DeviceDiscountPinResponse(
    bool IsSet, long? Version, string? Algorithm, int? Iterations, string? Salt, string? Hash, DateTimeOffset? ChangedAtUtc);

/// <summary>
/// Npgsql-backed store for the one discount PIN a branch has
/// (branch-discount-pin spec), shaped like <see cref="PostgresCategoryStore"/>:
/// each method owns its transaction, `set_config` is the first statement, and a
/// write audits in the same transaction. The table is branch-owned, so every
/// call needs a scope that carries the branch; a scope for another branch or
/// organization simply sees no row. Only the hash, salt and parameters are
/// stored; the audit record carries versions only, never the PIN or its hash.
/// </summary>
public sealed class PostgresBranchDiscountPinStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBranchDiscountPinStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>True when the branch exists in the scope organization (branches are organization-scoped).</summary>
    public async Task<bool> BranchExistsAsync(CloudTenantScope scope, Guid branchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, branchId: null, ct);

        await using var cmd = new NpgsqlCommand("SELECT 1 FROM branches WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(branchId);
        var exists = await cmd.ExecuteScalarAsync(ct) is not null;
        await tx.CommitAsync(ct);
        return exists;
    }

    /// <summary>
    /// Sets or rotates the PIN of <paramref name="branchId"/>. The caller must
    /// already have checked that the branch belongs to the scope organization.
    /// </summary>
    public async Task<BranchDiscountPinStatus> SetAsync(
        CloudTenantScope scope, Guid branchId, DiscountPinVerifier verifier, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, branchId, ct);

        long version;
        DateTimeOffset changedAt;
        bool inserted;
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO branch_discount_pins
                (branch_id, organization_id, algorithm, iterations, salt, pin_hash, version, rotated_at_utc, rotated_by)
            VALUES ($1, $2, $3, $4, $5, $6, 1, now(), $7)
            ON CONFLICT (branch_id) DO UPDATE SET
                algorithm = excluded.algorithm,
                iterations = excluded.iterations,
                salt = excluded.salt,
                pin_hash = excluded.pin_hash,
                version = branch_discount_pins.version + 1,
                rotated_at_utc = now(),
                rotated_by = excluded.rotated_by
            RETURNING version, rotated_at_utc, (xmax = 0)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(scope.OrganizationId);
            cmd.Parameters.AddWithValue(verifier.Algorithm);
            cmd.Parameters.AddWithValue(verifier.Iterations);
            cmd.Parameters.AddWithValue(verifier.Salt);
            cmd.Parameters.AddWithValue(verifier.Hash);
            cmd.Parameters.AddWithValue(actorId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            version = reader.GetInt64(0);
            changedAt = reader.GetFieldValue<DateTimeOffset>(1);
            inserted = reader.GetBoolean(2);
        }

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "branch-discount-pin", branchId,
                inserted ? "branch.discount-pin.set" : "branch.discount-pin.rotated",
                OldValueJson: inserted ? null : JsonSerializer.Serialize(new { version = version - 1 }),
                NewValueJson: JsonSerializer.Serialize(new { version })),
            ct);

        await tx.CommitAsync(ct);
        return new BranchDiscountPinStatus(true, version, changedAt);
    }

    public async Task<BranchDiscountPinStatus> GetStatusAsync(CloudTenantScope scope, Guid branchId, CancellationToken ct)
    {
        var row = await ReadAsync(scope, branchId, ct);
        return row is null ? new BranchDiscountPinStatus(false, null, null) : new BranchDiscountPinStatus(true, row.Value.Version, row.Value.ChangedAtUtc);
    }

    public async Task<DeviceDiscountPinResponse> GetVerifierAsync(CloudTenantScope scope, Guid branchId, CancellationToken ct)
    {
        var row = await ReadAsync(scope, branchId, ct);
        return row is null
            ? new DeviceDiscountPinResponse(false, null, null, null, null, null, null)
            : new DeviceDiscountPinResponse(
                true, row.Value.Version, row.Value.Verifier.Algorithm, row.Value.Verifier.Iterations,
                Convert.ToBase64String(row.Value.Verifier.Salt), Convert.ToBase64String(row.Value.Verifier.Hash), row.Value.ChangedAtUtc);
    }

    private async Task<(long Version, DateTimeOffset ChangedAtUtc, DiscountPinVerifier Verifier)?> ReadAsync(
        CloudTenantScope scope, Guid branchId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await TenantScopeSql.ApplyAsync(connection, tx, scope.OrganizationId, branchId, ct);

        (long, DateTimeOffset, DiscountPinVerifier)? result = null;
        await using (var cmd = new NpgsqlCommand(
            "SELECT version, rotated_at_utc, algorithm, iterations, salt, pin_hash FROM branch_discount_pins WHERE branch_id = $1",
            connection, tx))
        {
            cmd.Parameters.AddWithValue(branchId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                result = (reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1),
                    new DiscountPinVerifier(reader.GetString(2), reader.GetInt32(3), (byte[])reader[4], (byte[])reader[5]));
            }
        }

        await tx.CommitAsync(ct);
        return result;
    }
}
