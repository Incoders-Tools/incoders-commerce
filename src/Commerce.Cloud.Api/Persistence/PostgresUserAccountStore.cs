using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Outcome of <see cref="PostgresUserAccountStore.CreateStaffUserAsync"/>
/// (commerce-role-taxonomy design.md "Data Flow" — create staff user).
/// </summary>
public enum CreateStaffUserOutcome
{
    Created,
    DuplicateEmail,
}

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
            "SELECT id, organization_id, email, password_hash, is_revoked, session_version FROM users WHERE email = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(normalized);

        UserCredentialRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = new UserCredentialRecord(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
                    reader.GetInt32(5));
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
            "SELECT organization_id, branch_scope, roles, is_revoked, customer_id FROM users WHERE id = $1",
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
                var customerId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);

                var roleDtos = JsonSerializer.Deserialize<List<RoleDto>>(rolesJson, RoleSerializerOptions) ?? [];
                var roles = roleDtos.Select(r => new Role(r.Name, r.Permissions));

                // commerce-customer-identity: CustomerId must be loaded here so
                // every authorization site reading LoadActorAsync's result
                // (sign-in, /account/me, ManageUsers-gated endpoints) sees the
                // same denied-by-construction EffectivePermissions the domain
                // guard already enforces.
                actor = new UserAccount(userId, organizationId, branchScope, roles, customerId);
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
    /// Thin wrapper (design.md "Transaction composition"): open -> begin ->
    /// set tenant scope -> zero-users guard -> <see cref="InsertAsync"/> ->
    /// commit. Behavior-preserving refactor extraction target — every
    /// existing caller and test keeps its current behavior and signature.
    /// </summary>
    public async Task<bool> TryCreateAsync(CloudTenantScope scope, NewUserAccount user, CancellationToken ct)
    {
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

        try
        {
            await InsertAsync(connection, tx, scope, user, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Transaction-participating insert (design.md "Transaction composition"
    /// — the core decision): carries the exact `users` + `user_directory`
    /// insert bodies <see cref="TryCreateAsync"/> used to own inline, minus
    /// ALL tx lifecycle and `set_config`. The caller owns the connection,
    /// the transaction, and tenant scoping; this method never commits or
    /// rolls back and never calls `set_config`. Throws
    /// <see cref="PostgresException"/> (unique violation) on a duplicate
    /// email so the OWNING transaction decides what to do — this lets
    /// <see cref="PostgresOrganizationStore"/> participate in the same
    /// transaction as its own organization/branch inserts (single owner of
    /// the transaction, zero duplicated INSERT SQL).
    /// </summary>
    internal async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, NewUserAccount user, CancellationToken ct)
    {
        var normalizedEmail = Normalize(user.Email);
        var rolesJson = JsonSerializer.Serialize(user.Roles, RoleSerializerOptions);

        await using (var insertUserCmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles, customer_id)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7)
            """, connection, tx))
        {
            insertUserCmd.Parameters.AddWithValue(user.Id);
            insertUserCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertUserCmd.Parameters.AddWithValue(normalizedEmail);
            insertUserCmd.Parameters.AddWithValue(user.PasswordHash);
            insertUserCmd.Parameters.AddWithValue(user.BranchScope.ToArray());
            insertUserCmd.Parameters.AddWithValue(rolesJson);
            insertUserCmd.Parameters.AddWithValue((object?)user.CustomerId ?? DBNull.Value);
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

    /// <summary>
    /// Confirms every id in <paramref name="branchIds"/> resolves to a
    /// branch visible under <paramref name="scope"/> (commerce-role-taxonomy
    /// design.md "Data Flow" — "branchIds not a subset of caller-org
    /// branches -> 400"). RLS already filters out any branch belonging to
    /// another organization, so a cross-org or nonexistent id simply fails
    /// to be counted — no explicit organization_id comparison needed.
    /// </summary>
    public async Task<bool> BranchesBelongToOrganizationAsync(
        CloudTenantScope scope, IReadOnlyList<Guid> branchIds, CancellationToken ct)
    {
        if (branchIds.Count == 0)
        {
            return true;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM branches WHERE id = ANY($1)", connection, tx);
        cmd.Parameters.AddWithValue(branchIds.ToArray());
        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;

        await tx.CommitAsync(ct);
        return count == branchIds.Count;
    }

    /// <summary>
    /// Owns ONE transaction (commerce-role-taxonomy design.md "Audit write
    /// mechanism"): set_config -> <see cref="InsertAsync"/> -> the audit row
    /// -> COMMIT. A duplicate-email unique violation rolls the whole
    /// transaction back BEFORE the audit insert is ever attempted, so no
    /// audit row is ever written for a failed create.
    /// </summary>
    public async Task<CreateStaffUserOutcome> CreateStaffUserAsync(
        CloudTenantScope scope, NewUserAccount user, UserManagementAuditEntry auditEntry, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        try
        {
            await InsertAsync(connection, tx, scope, user, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return CreateStaffUserOutcome.DuplicateEmail;
        }

        await AuditLogWriter.InsertAsync(connection, tx, auditEntry, ct);
        await tx.CommitAsync(ct);
        return CreateStaffUserOutcome.Created;
    }

    /// <summary>
    /// Reads the CURRENT persisted role names for <paramref name="userId"/>
    /// inside the caller's own transaction — used by
    /// <see cref="ReplaceRolesAsync"/> to record the audit row's old value
    /// atomically with the update, avoiding a TOCTOU gap against a separate
    /// earlier read.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ListRoleNamesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT roles FROM users WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(userId);

        var rolesJson = (string?)await cmd.ExecuteScalarAsync(ct);
        if (rolesJson is null)
        {
            return [];
        }

        var dtos = JsonSerializer.Deserialize<List<RoleDto>>(rolesJson, RoleSerializerOptions) ?? [];
        return dtos.Select(d => d.Name).ToList();
    }

    /// <summary>
    /// Owns ONE transaction: set_config -> read prior role names -> UPDATE
    /// -> audit row (old = prior names, new = requested names) -> COMMIT
    /// (commerce-role-taxonomy design.md "Data Flow" — assign roles).
    /// </summary>
    public async Task ReplaceRolesAsync(
        CloudTenantScope scope, Guid userId, IReadOnlyList<RoleDto> roles, string actorKind, Guid actorId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var priorRoleNames = await ListRoleNamesAsync(connection, tx, userId, ct);

        var rolesJson = JsonSerializer.Serialize(roles, RoleSerializerOptions);
        await using (var cmd = new NpgsqlCommand("UPDATE users SET roles = $1::jsonb WHERE id = $2", connection, tx))
        {
            cmd.Parameters.AddWithValue(rolesJson);
            cmd.Parameters.AddWithValue(userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var oldValueJson = JsonSerializer.Serialize(priorRoleNames, RoleSerializerOptions);
        var newValueJson = JsonSerializer.Serialize(roles.Select(r => r.Name), RoleSerializerOptions);

        await AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry(
                actorKind, actorId, scope.OrganizationId, "user", userId, "user.roles.assigned", oldValueJson, newValueJson),
            ct);

        await tx.CommitAsync(ct);
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
