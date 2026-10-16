using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-password-recovery task 2.1: `SessionVersionCache` TTL
/// expiry and write-through `Set`, against a LIVE Postgres instance as the
/// DB-read-on-miss backend (design.md "Session invalidation"). No standalone
/// unit-test project exists in this repo (see `tests/Commerce.Integration`
/// convention for every other store-adjacent class), so this lives alongside
/// the other Postgres-backed tests with an injected clock standing in for
/// the "fake loader" the design's Testing Strategy describes.
/// </summary>
[Collection("Postgres")]
public sealed class SessionVersionCacheTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public SessionVersionCacheTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var root = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        foreach (var file in new[]
                 {
                     "0002_users.sql", "0003_organizations_branches.sql",
                     "0004_device_credentials.sql", "0005_password_recovery.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", file));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(CloudTenantScope Scope, Guid UserId)> SeedUserAsync()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using (var orgCmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner))
        {
            orgCmd.Parameters.AddWithValue(organizationId);
            orgCmd.ExecuteNonQuery();
        }
        using (var branchCmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", owner))
        {
            branchCmd.Parameters.AddWithValue(branchId);
            branchCmd.Parameters.AddWithValue(organizationId);
            branchCmd.ExecuteNonQuery();
        }

        var scope = new CloudTenantScope(organizationId);
        var userStore = new PostgresUserAccountStore(_dataSource!);
        await userStore.TryCreateAsync(
            scope,
            new NewUserAccount(userId, "cache@example.com", "hash", [branchId], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None);

        return (scope, userId);
    }

    [Fact]
    public async Task GetAsync_OnMiss_ReadsFromStore_AndCachesTheResult()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var cache = new SessionVersionCache(store, () => now);

        var version = await cache.GetAsync(scope, userId, CancellationToken.None);

        Assert.Equal(0, version);
    }

    [Fact]
    public async Task Set_WriteThrough_IsVisibleImmediately_WithoutADbRead()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var cache = new SessionVersionCache(store, () => now);

        // Write-through: bump the cached value directly, WITHOUT touching the
        // DB row (still 0 there) — GetAsync must return the cached value.
        cache.Set(userId, 7);
        var version = await cache.GetAsync(scope, userId, CancellationToken.None);

        Assert.Equal(7, version);
    }

    [Fact]
    public async Task GetAsync_AfterTtlExpiry_ReloadsFromStore()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var cache = new SessionVersionCache(store, () => clockBox[0]);

        cache.Set(userId, 7);
        // The DB row's real value is bumped to 1 out-of-band (simulating a
        // password change on another instance/process).
        await store.SetPasswordAsync(scope, userId, "new-hash", CancellationToken.None);

        // Still within the 60s TTL: stale cached value wins.
        var stillCached = await cache.GetAsync(scope, userId, CancellationToken.None);
        Assert.Equal(7, stillCached);

        // Past the 60s TTL: must reload and see the real DB value.
        clockBox[0] = now.AddSeconds(61);
        var reloaded = await cache.GetAsync(scope, userId, CancellationToken.None);
        Assert.Equal(1, reloaded);
    }
}
