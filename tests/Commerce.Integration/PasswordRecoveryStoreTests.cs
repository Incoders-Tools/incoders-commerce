using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-password-recovery task 1.5: `PostgresPasswordRecoveryStore`
/// against a LIVE Postgres instance (`deploy/dev/compose.yaml`), connecting as
/// the non-owner `app_runtime` role. If compose is not running, these tests
/// report the gap clearly and return without asserting pass/fail — see
/// `PostgresTestFixture`.
/// </summary>
[Collection("Postgres")]
public sealed class PasswordRecoveryStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PasswordRecoveryStoreTests()
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
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
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

    private async Task<(CloudTenantScope Scope, Guid UserId)> SeedUserAsync(NpgsqlDataSource dataSource)
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
        var userStore = new PostgresUserAccountStore(dataSource);
        var created = await userStore.TryCreateAsync(
            scope,
            new NewUserAccount(userId, "recovery@example.com", "hash", [branchId], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None);
        Assert.True(created);

        return (scope, userId);
    }

    [Fact]
    public async Task IssueTokenAsync_ThenFindTokenAsync_Unscoped_ResolvesRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync(_dataSource!);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        await store.IssueTokenAsync(scope, userId, "hash-issue-find", expiresAt, CancellationToken.None);

        var record = await store.FindTokenAsync("hash-issue-find", CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(userId, record!.UserId);
        Assert.Equal(scope.OrganizationId, record.OrganizationId);
        Assert.Null(record.ConsumedAt);
    }

    [Fact]
    public async Task FindTokenAsync_UnknownHash_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);

        var record = await store.FindTokenAsync("does-not-exist", CancellationToken.None);

        Assert.Null(record);
    }

    [Fact]
    public async Task ConsumeAndSetPasswordAsync_UpdatesHash_ConsumesToken_AndBumpsSessionVersion()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync(_dataSource!);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await store.IssueTokenAsync(scope, userId, "hash-consume", expiresAt, CancellationToken.None);

        var newVersion = await store.ConsumeAndSetPasswordAsync(
            scope, userId, "hash-consume", "new-hash", CancellationToken.None);

        Assert.Equal(1, newVersion);

        var afterConsume = await store.FindTokenAsync("hash-consume", CancellationToken.None);
        Assert.NotNull(afterConsume!.ConsumedAt);

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var credential = await userStore.FindByEmailAsync(scope, "recovery@example.com", CancellationToken.None);
        Assert.Equal("new-hash", credential!.PasswordHash);
        Assert.Equal(1, credential.SessionVersion);
    }

    [Fact]
    public async Task SetPasswordAsync_UpdatesHash_AndBumpsSessionVersion_WithoutAToken()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync(_dataSource!);

        var newVersion = await store.SetPasswordAsync(scope, userId, "admin-set-hash", CancellationToken.None);

        Assert.Equal(1, newVersion);

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var credential = await userStore.FindByEmailAsync(scope, "recovery@example.com", CancellationToken.None);
        Assert.Equal("admin-set-hash", credential!.PasswordHash);
        Assert.Equal(1, credential.SessionVersion);
    }

    [Fact]
    public async Task GetSessionVersionAsync_ReturnsCurrentVersion()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var (scope, userId) = await SeedUserAsync(_dataSource!);

        var initial = await store.GetSessionVersionAsync(scope, userId, CancellationToken.None);
        Assert.Equal(0, initial);

        await store.SetPasswordAsync(scope, userId, "bump-hash", CancellationToken.None);

        var afterBump = await store.GetSessionVersionAsync(scope, userId, CancellationToken.None);
        Assert.Equal(1, afterBump);
    }
}
