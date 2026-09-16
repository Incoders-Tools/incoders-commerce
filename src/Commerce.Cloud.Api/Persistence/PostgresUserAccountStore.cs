using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed credential store (design.md "Interfaces / Contracts"),
/// following <see cref="PostgresCloudInboxStore"/>'s exact shape: raw
/// <see cref="NpgsqlDataSource"/>, every scoped method opens its own
/// <see cref="NpgsqlTransaction"/>, and `SELECT set_config('app.current_org_id', $1, true)`
/// is always the FIRST statement in that transaction before the tenant-scoped
/// query — except <see cref="FindDirectoryEntryAsync"/>, which is
/// deliberately UNSCOPED (the only method that does not call set_config)
/// because it resolves the organization the sign-in flow does not know yet.
/// </summary>
public sealed class PostgresUserAccountStore
{
    private static readonly JsonSerializerOptions RoleSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserAccountStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// Unscoped by design: resolves an email to the organization it belongs
    /// to via `user_directory`'s `USING (true)` read policy. Returns no
    /// credential material.
    /// </summary>
    public async Task<UserDirectoryEntry?> FindDirectoryEntryAsync(string email, CancellationToken ct)
    {
        var normalized = Normalize(email);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT email_normalized, organization_id, user_id FROM user_directory WHERE email_normalized = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(normalized);

        UserDirectoryEntry? entry = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                entry = new UserDirectoryEntry(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2));
            }
        }

        await tx.CommitAsync(ct);
        return entry;
    }

    public async Task<UserCredentialRecord?> FindByEmailAsync(CloudTenantScope scope, string email, CancellationToken ct)
    {
        var normalized = Normalize(email);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT id, organization_id, email, password_hash, is_revoked FROM users WHERE email = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(normalized);

        UserCredentialRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = new UserCredentialRecord(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<UserAccount?> LoadActorAsync(CloudTenantScope scope, Guid userId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT organization_id, branch_scope, roles, is_revoked FROM users WHERE id = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(userId);

        UserAccount? actor = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var organizationId = reader.GetGuid(0);
                var branchScope = reader.GetFieldValue<Guid[]>(1);
                var rolesJson = reader.GetString(2);
                var isRevoked = reader.GetBoolean(3);

                var roleDtos = JsonSerializer.Deserialize<List<RoleDto>>(rolesJson, RoleSerializerOptions) ?? [];
                var roles = roleDtos.Select(r => new Role(r.Name, r.Permissions));

                actor = new UserAccount(userId, organizationId, branchScope, roles);
                if (isRevoked)
                {
                    actor.Revoke();
                }
            }
        }

        await tx.CommitAsync(ct);
        return actor;
    }

    public async Task<bool> HasAnyUserAsync(CloudTenantScope scope, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", connection, tx);
        var exists = (bool)(await cmd.ExecuteScalarAsync(ct))!;

        await tx.CommitAsync(ct);
        return exists;
    }

    /// <summary>
    /// Inserts into `users` and `user_directory` inside ONE transaction,
    /// after the scope statement, re-checking "zero users in this org" in
    /// that same transaction. `user_directory`'s primary key makes a
    /// duplicate email a clean `false`, never an exception surfaced to the
    /// caller.
    /// </summary>
    public async Task<bool> TryCreateAsync(CloudTenantScope scope, NewUserAccount user, CancellationToken ct)
    {
        var normalizedEmail = Normalize(user.Email);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        // Bootstrap invariant (design.md "Bootstrap"): re-check "zero users in
        // this org" inside the SAME transaction as the insert, immediately
        // before writing, to close the race window between the caller's
        // earlier HasAnyUserAsync check and this write.
        await using (var existsCmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", connection, tx))
        {
            var alreadyHasUsers = (bool)(await existsCmd.ExecuteScalarAsync(ct))!;
            if (alreadyHasUsers)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }

        var rolesJson = JsonSerializer.Serialize(user.Roles, RoleSerializerOptions);

        try
        {
            await using (var insertUserCmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb)
                """, connection, tx))
            {
                insertUserCmd.Parameters.AddWithValue(user.Id);
                insertUserCmd.Parameters.AddWithValue(scope.OrganizationId);
                insertUserCmd.Parameters.AddWithValue(normalizedEmail);
                insertUserCmd.Parameters.AddWithValue(user.PasswordHash);
                insertUserCmd.Parameters.AddWithValue(user.BranchScope.ToArray());
                insertUserCmd.Parameters.AddWithValue(rolesJson);
                await insertUserCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var insertDirectoryCmd = new NpgsqlCommand(
                "INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1, $2, $3)",
                connection, tx))
            {
                insertDirectoryCmd.Parameters.AddWithValue(normalizedEmail);
                insertDirectoryCmd.Parameters.AddWithValue(scope.OrganizationId);
                insertDirectoryCmd.Parameters.AddWithValue(user.Id);
                await insertDirectoryCmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Tenant scoping is ALWAYS the first statement inside the transaction,
    /// mirroring <see cref="PostgresCloudInboxStore"/> exactly — see that
    /// class's remarks for the pooler-safety rationale.
    /// </summary>
    private static async Task SetTenantScopeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await using var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx);
        scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
        await scopeCmd.ExecuteNonQueryAsync(ct);
    }
}
