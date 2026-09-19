using Commerce.Cloud.Api.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Credential-free-at-rest record used to verify a platform-admin sign-in
/// (commerce-role-taxonomy design.md "Interfaces / Contracts").
/// </summary>
public sealed record PlatformAdminCredentialRecord(Guid Id, string Email, string PasswordHash);

/// <summary>
/// Real Npgsql-backed platform-admin store (design.md "Interfaces /
/// Contracts" / "Platform-admin write path"). <see cref="FindByEmailAsync"/>
/// is deliberately UNSCOPED — <c>platform_admins_lookup</c>'s
/// <c>USING (true)</c> policy is the only read policy on the table, and
/// there is no tenant dimension to scope by. <see cref="TryCreateGenesisAsync"/>
/// relies on the database's <c>NOT EXISTS</c> INSERT policy as the SECOND,
/// independent barrier against a second platform admin — even a bug in the
/// caller cannot make a second row representable.
/// <see cref="ListOrganizationsAsync"/> is the ONLY genuinely cross-org read
/// in the system: it uses the keyed, least-privilege
/// <c>platform_readonly</c> datasource, never the shared <c>app_runtime</c>
/// pool. When that datasource is not configured (`ConnectionStrings:CommercePlatformRead`
/// absent), <see cref="CanListOrganizations"/> is false and the caller must
/// fail closed with 503 — this store never falls back to `app_runtime`.
/// </summary>
public sealed class PostgresPlatformAdminStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlDataSource? _platformReadDataSource;

    public PostgresPlatformAdminStore(
        NpgsqlDataSource dataSource,
        [FromKeyedServices(PlatformReadDataSourceKey)] NpgsqlDataSource? platformReadDataSource = null)
    {
        _dataSource = dataSource;
        _platformReadDataSource = platformReadDataSource;
    }

    public const string PlatformReadDataSourceKey = "platform-read";

    public bool CanListOrganizations => _platformReadDataSource is not null;

    public async Task<PlatformAdminCredentialRecord?> FindByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = Normalize(email);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, email, password_hash FROM platform_admins WHERE email = $1", connection);
        cmd.Parameters.AddWithValue(normalized);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PlatformAdminCredentialRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task<bool> HasAnyAdminAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM platform_admins)", connection);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Succeeds ONCE. A second call — even direct, bypassing the API — is
    /// rejected by the `platform_admins_genesis` policy's
    /// `WITH CHECK (NOT EXISTS (SELECT 1 FROM platform_admins))`, which
    /// surfaces as a row-level-security policy violation
    /// (<see cref="PostgresException"/>).
    /// </summary>
    public async Task<bool> TryCreateGenesisAsync(Guid id, string email, string passwordHash, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO platform_admins (id, email, password_hash) VALUES ($1, $2, $3)", connection);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(Normalize(email));
        cmd.Parameters.AddWithValue(passwordHash);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (PostgresException)
        {
            // Either the genesis-only RLS policy rejected a second row, or a
            // duplicate-email unique violation — either way, no admin was
            // created by this call.
            return false;
        }
    }

    /// <summary>
    /// Column-scoped by grant (design.md "platform_admins table shape and
    /// RLS"): `app_runtime` may only update `last_sign_in_at_utc`.
    /// </summary>
    public async Task TouchLastSignInAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE platform_admins SET last_sign_in_at_utc = now() WHERE id = $1", connection);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The ONLY cross-organization read in the system. Throws
    /// <see cref="InvalidOperationException"/> if called while
    /// <see cref="CanListOrganizations"/> is false — callers MUST check that
    /// first and fail closed with 503 rather than ever falling back to
    /// `app_runtime`.
    /// </summary>
    public async Task<IReadOnlyList<OrganizationSummary>> ListOrganizationsAsync(CancellationToken ct)
    {
        if (_platformReadDataSource is null)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:CommercePlatformRead is not configured; cannot list organizations.");
        }

        await using var connection = await _platformReadDataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, created_at FROM organizations ORDER BY name", connection);

        var results = new List<OrganizationSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new OrganizationSummary(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return results;
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
