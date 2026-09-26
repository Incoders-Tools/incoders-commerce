using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed reset-token store (commerce-password-recovery
/// design.md "Interfaces / Contracts"), following
/// <see cref="PostgresUserAccountStore"/>'s exact shape: injected
/// <see cref="NpgsqlDataSource"/>, every scoped method opens its own
/// <see cref="NpgsqlTransaction"/> with `set_config('app.current_org_id', ...)`
/// as the first statement — except <see cref="FindTokenAsync"/>, which is
/// deliberately UNSCOPED because confirm has no org until the token row is
/// read.
/// </summary>
public sealed class PostgresPasswordRecoveryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPasswordRecoveryStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task IssueTokenAsync(
        CloudTenantScope scope, Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO password_reset_tokens (token_hash, user_id, organization_id, expires_at)
            VALUES ($1, $2, $3, $4)
            """, connection, tx))
        {
            insertCmd.Parameters.AddWithValue(tokenHash);
            insertCmd.Parameters.AddWithValue(userId);
            insertCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertCmd.Parameters.AddWithValue(expiresAt);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        // Same tx: consume all OTHER prior unconsumed tokens for this user
        // (design.md Data Flow "same tx: consume all prior unconsumed tokens
        // for this user") so an earlier reset request cannot be replayed
        // after a newer one is issued.
        await using (var supersedeCmd = new NpgsqlCommand(
            """
            UPDATE password_reset_tokens SET consumed_at = now()
            WHERE user_id = $1 AND consumed_at IS NULL AND token_hash != $2
            """, connection, tx))
        {
            supersedeCmd.Parameters.AddWithValue(userId);
            supersedeCmd.Parameters.AddWithValue(tokenHash);
            await supersedeCmd.ExecuteNonQueryAsync(ct);
        }

        // Opportunistic bounded-growth sweep (design.md "Token cleanup").
        await using (var purgeCmd = new NpgsqlCommand(
            "DELETE FROM password_reset_tokens WHERE expires_at < now() - interval '7 days'", connection, tx))
        {
            await purgeCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Deliberately UNSCOPED (no `set_config` at all): confirm resolves the
    /// token row via `password_reset_tokens_lookup`'s `USING (true)` policy
    /// BEFORE any tenant scope is known.
    /// </summary>
    public async Task<PasswordResetTokenRecord?> FindTokenAsync(string tokenHash, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT token_hash, user_id, organization_id, requested_at, expires_at, consumed_at FROM password_reset_tokens WHERE token_hash = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(tokenHash);

        PasswordResetTokenRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = new PasswordResetTokenRecord(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// Single tx (design.md Data Flow "Reset confirm"): updates the password
    /// hash and bumps `session_version`, then marks the token consumed.
    /// Returns the new session version.
    /// </summary>
    public async Task<int> ConsumeAndSetPasswordAsync(
        CloudTenantScope scope, Guid userId, string tokenHash, string passwordHash, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var newVersion = await UpdatePasswordAsync(connection, tx, userId, passwordHash, ct);

        await using (var consumeCmd = new NpgsqlCommand(
            "UPDATE password_reset_tokens SET consumed_at = now() WHERE user_id = $1 AND consumed_at IS NULL",
            connection, tx))
        {
            consumeCmd.Parameters.AddWithValue(userId);
            await consumeCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return newVersion;
    }

    /// <summary>
    /// No token involved (design.md Data Flow "Renew" / "Admin-forced
    /// reset"). Updates the password hash and bumps `session_version` in one
    /// transaction. Returns the new session version.
    /// </summary>
    public async Task<int> SetPasswordAsync(CloudTenantScope scope, Guid userId, string passwordHash, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        var newVersion = await UpdatePasswordAsync(connection, tx, userId, passwordHash, ct);

        await tx.CommitAsync(ct);
        return newVersion;
    }

    public async Task<int?> GetSessionVersionAsync(CloudTenantScope scope, Guid userId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await SetTenantScopeAsync(connection, tx, scope, ct);

        await using var cmd = new NpgsqlCommand("SELECT session_version FROM users WHERE id = $1", connection, tx);
        cmd.Parameters.AddWithValue(userId);
        var result = await cmd.ExecuteScalarAsync(ct);

        await tx.CommitAsync(ct);
        return result is null ? null : (int)result;
    }

    private static async Task<int> UpdatePasswordAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid userId, string passwordHash, CancellationToken ct)
    {
        await using var updateCmd = new NpgsqlCommand(
            """
            UPDATE users SET password_hash = $1, session_version = session_version + 1
            WHERE id = $2
            RETURNING session_version
            """, connection, tx);
        updateCmd.Parameters.AddWithValue(passwordHash);
        updateCmd.Parameters.AddWithValue(userId);

        var result = await updateCmd.ExecuteScalarAsync(ct);
        if (result is null)
        {
            throw new InvalidOperationException($"No user found with id {userId} to update the password for.");
        }

        return (int)result;
    }

    /// <summary>
    /// Tenant scoping is ALWAYS the first statement inside the transaction,
    /// mirroring <see cref="PostgresUserAccountStore"/> exactly.
    /// </summary>
    private static async Task SetTenantScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, CloudTenantScope scope, CancellationToken ct)
    {
        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);
    }
}
