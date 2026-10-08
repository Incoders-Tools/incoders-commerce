using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Ordering;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Real Npgsql-backed <see cref="ICustomerOrderingAccessResolver"/> (design.md
/// "Interfaces / Contracts"). The credential's hash storage mirrors
/// <c>device_credentials</c> (0004) exactly, including its hashing rationale:
/// <see cref="Guid"/> is a 122-bit uniformly random value, so plain SHA-256 is
/// correct and a slow KDF would add latency to every order submission for
/// zero gain — reusing <see cref="DeviceTokenHasher.Hash"/> rather than
/// authoring a second crypto surface for the same property.
///
/// <see cref="ResolveAsync"/> is deliberately UNSCOPED (mirrors
/// <see cref="PostgresDeviceCredentialStore.FindByTokenHashAsync"/> and the
/// `customer_ordering_access_lookup` policy's <c>USING (true)</c>): it
/// resolves by hash alone and hands the row's own <c>organization_id</c> back
/// so the caller can compare it, never filtering the lookup. <see cref="IssueAsync"/>
/// is scoped (INSERT policy pins <c>organization_id</c>); <see cref="RevokeAsync"/>
/// is unscoped (UPDATE policy allows it only when the result is revoked, so
/// an unscoped un-revoke is unrepresentable).
/// </summary>
public sealed class PostgresCustomerOrderingAccessStore : ICustomerOrderingAccessResolver
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCustomerOrderingAccessStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private static string Hash(Guid credential) => DeviceTokenHasher.Hash(credential.ToString());

    /// <summary>
    /// UNSCOPED by design: the request does not yet carry a proven tenant
    /// scope for this credential — that is exactly what this lookup decides.
    /// The caller (<see cref="CustomerCatalogAccessService"/>) is the only
    /// place the organization comparison happens.
    /// </summary>
    public async Task<CustomerOrderingAccess?> ResolveAsync(Guid organizationId, Guid credential, CancellationToken ct)
    {
        var credentialHash = Hash(credential);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT organization_id, customer_id, is_enabled FROM customer_ordering_access WHERE credential_hash = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(credentialHash);

        CustomerOrderingAccess? access = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                access = new CustomerOrderingAccess(
                    reader.GetGuid(0), reader.GetGuid(1), credential, reader.GetBoolean(2));
            }
        }

        await tx.CommitAsync(ct);
        return access;
    }

    /// <summary>
    /// ONE transaction: set_config -> INSERT (WITH CHECK pins organization_id)
    /// -> COMMIT. Returns the plaintext credential exactly once — it is never
    /// stored (only its hash is) and never reconstructable from the row.
    /// </summary>
    public async Task<Guid> IssueAsync(CloudTenantScope scope, Guid customerId, Guid issuedByUserId, CancellationToken ct)
    {
        var credential = Guid.NewGuid();
        var credentialHash = Hash(credential);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await TenantScopeSql.ApplyAsync(connection, tx, scope, ct);

        await using (var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO customer_ordering_access
                (credential_hash, organization_id, customer_id, issued_by_user_id)
            VALUES ($1, $2, $3, $4)
            """, connection, tx))
        {
            insertCmd.Parameters.AddWithValue(credentialHash);
            insertCmd.Parameters.AddWithValue(scope.OrganizationId);
            insertCmd.Parameters.AddWithValue(customerId);
            insertCmd.Parameters.AddWithValue(issuedByUserId);
            await insertCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return credential;
    }

    /// <summary>
    /// Unscoped by design (`customer_ordering_access_revoke` policy: `USING
    /// (true) WITH CHECK (NOT is_enabled)`) — revocation must be immediately
    /// effective regardless of which org's connection issues it, and the
    /// WITH CHECK makes an unscoped un-revoke unrepresentable.
    /// </summary>
    public async Task<bool> RevokeAsync(Guid credential, CancellationToken ct)
    {
        var credentialHash = Hash(credential);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "UPDATE customer_ordering_access SET is_enabled = false, revoked_at_utc = now() WHERE credential_hash = $1",
            connection, tx);
        cmd.Parameters.AddWithValue(credentialHash);
        var rows = await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return rows > 0;
    }
}
