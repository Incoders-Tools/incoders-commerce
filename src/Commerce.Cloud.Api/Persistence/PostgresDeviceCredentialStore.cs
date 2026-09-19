using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed device credential store (design.md "Interfaces /
/// Contracts"), following <see cref="PostgresCloudInboxStore"/>'s exact
/// shape: raw <see cref="NpgsqlDataSource"/>, every scoped method opens its
/// own <see cref="NpgsqlTransaction"/>, `set_config` is always the first
/// statement — except <see cref="FindByTokenHashAsync"/>, which is
/// deliberately UNSCOPED (mirrors <c>PostgresUserAccountStore.FindDirectoryEntryAsync</c>
/// exactly) because it resolves the credential the request does not yet have
/// a tenant scope for.
/// </summary>
public sealed class PostgresDeviceCredentialStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDeviceCredentialStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>
    /// UNSCOPED by design (device_credentials_lookup USING (true)): resolves
    /// the credential the request does not yet have a tenant scope for. The
    /// caller (the authentication handler), not this method, decides what a
    /// revoked row means.
    /// </summary>
    public async Task<DeviceCredentialRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT id, organization_id, branch_id, installation_id, issued_to_user_id,
                   replaces_credential_id, is_revoked
            FROM device_credentials
            WHERE token_hash = $1
            """, connection, tx);
        cmd.Parameters.AddWithValue(tokenHash);

        DeviceCredentialRecord? record = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                record = new DeviceCredentialRecord(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    reader.GetBoolean(6));
            }
        }

        await tx.CommitAsync(ct);
        return record;
    }

    /// <summary>
    /// ONE transaction: set_config -> revoke every live credential for this
    /// installation (unscoped UPDATE, allowed only because the result is
    /// revoked, per the `device_credentials_revoke` policy) -> INSERT the new
    /// row (WITH CHECK pins organization_id) -> COMMIT. A terminal therefore
    /// never holds two live credentials, and re-pairing into a different
    /// organization cannot leave the prior organization's binding alive.
    /// </summary>
    public async Task<IssuedDeviceCredential> IssueAsync(
        CloudTenantScope scope, Guid installationId, Guid branchId, Guid issuedToUserId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx))
        {
            scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        Guid? replacesCredentialId = null;
        await using (var revokeCmd = new NpgsqlCommand(
            """
            UPDATE device_credentials
            SET is_revoked = true, revoked_at = now()
            WHERE installation_id = $1 AND NOT is_revoked
            RETURNING id
            """, connection, tx))
        {
            revokeCmd.Parameters.AddWithValue(installationId);
            await using var reader = await revokeCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                replacesCredentialId = reader.GetGuid(0);
            }
        }

        var credentialId = Guid.NewGuid();
        var plaintextToken = DeviceTokenHasher.GenerateSecret();
        var tokenHash = DeviceTokenHasher.Hash(plaintextToken);

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO device_credentials
                (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id, replaces_credential_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, connection, tx))
        {
            insertCmd.Parameters.AddWithValue(tokenHash);
            insertCmd.Parameters.AddWithValue(credentialId);
            insertCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertCmd.Parameters.AddWithValue(branchId);
            insertCmd.Parameters.AddWithValue(installationId);
            insertCmd.Parameters.AddWithValue(issuedToUserId);
            insertCmd.Parameters.AddWithValue((object?)replacesCredentialId ?? DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        var record = new DeviceCredentialRecord(
            credentialId, scope.OrganizationId, branchId, installationId, issuedToUserId, replacesCredentialId, IsRevoked: false);
        return new IssuedDeviceCredential(record, plaintextToken);
    }

    /// <summary>
    /// Scoped revocation for an operator-triggered revoke (not exercised by
    /// the pairing flow, which revokes unscoped inside <see cref="IssueAsync"/>).
    /// </summary>
    public async Task<bool> RevokeAsync(CloudTenantScope scope, Guid credentialId, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var scopeCmd = new NpgsqlCommand(
            "SELECT set_config('app.current_org_id', $1, true)", connection, tx))
        {
            scopeCmd.Parameters.AddWithValue(scope.OrganizationId.ToString());
            await scopeCmd.ExecuteNonQueryAsync(ct);
        }

        await using var updateCmd = new NpgsqlCommand(
            "UPDATE device_credentials SET is_revoked = true, revoked_at = now() WHERE id = $1",
            connection, tx);
        updateCmd.Parameters.AddWithValue(credentialId);
        var rows = await updateCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return rows > 0;
    }
}
