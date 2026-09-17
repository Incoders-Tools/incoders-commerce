using Commerce.Cloud.Api.Persistence;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy task 4.2: <see cref="PostgresPlatformAdminStore"/>
/// against a live Postgres instance (`deploy/dev/compose.yaml`). If Postgres
/// is not reachable, these tests report the gap and return without asserting
/// pass/fail, matching the repo's existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresPlatformAdminStoreTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static void ApplyAllMigrationsAndReset(NpgsqlConnection ownerConnection)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Commerce.sln).");
        }

        foreach (var fileName in new[]
                 {
                     "0001_init_rls.sql",
                     "0002_users.sql",
                     "0003_organizations_branches.sql",
                     "0004_device_credentials.sql",
                     "0005_password_recovery.sql",
                     "0006_role_taxonomy.sql",
                     "0007_platform_administration.sql",
                 })
        {
            var path = Path.Combine(dir.FullName, "deploy", "db", "migrations", fileName);
            var sql = File.ReadAllText(path)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", MigrationRlsTests.PlatformReadonlyPassword);
            using var cmd = new NpgsqlCommand(sql, ownerConnection);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE platform_admins, audit_log RESTART IDENTITY; " +
            "TRUNCATE TABLE organizations, branches, users, user_directory CASCADE", ownerConnection);
        resetCmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task FindByEmailAsync_IsUnscoped_FindsAnyPlatformAdminByEmail()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        ApplyAllMigrationsAndReset(owner);

        var store = new PostgresPlatformAdminStore(NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString));
        var id = Guid.NewGuid();
        var created = await store.TryCreateGenesisAsync(id, "find-me@example.com", "irrelevant-hash", CancellationToken.None);
        Assert.True(created);

        var found = await store.FindByEmailAsync("find-me@example.com", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);

        var notFound = await store.FindByEmailAsync("nobody@example.com", CancellationToken.None);
        Assert.Null(notFound);
    }

    [Fact]
    public async Task TryCreateGenesisAsync_SucceedsOnce_RejectsASecondAdmin_EvenCalledDirectly()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        ApplyAllMigrationsAndReset(owner);

        var store = new PostgresPlatformAdminStore(NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString));

        var first = await store.TryCreateGenesisAsync(Guid.NewGuid(), "genesis-1@example.com", "hash-1", CancellationToken.None);
        Assert.True(first);

        var second = await store.TryCreateGenesisAsync(Guid.NewGuid(), "genesis-2@example.com", "hash-2", CancellationToken.None);
        Assert.False(second);

        Assert.Null(await store.FindByEmailAsync("genesis-2@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task TouchLastSignInAsync_UpdatesOnlyLastSignInColumn()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        ApplyAllMigrationsAndReset(owner);

        var store = new PostgresPlatformAdminStore(NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString));
        var id = Guid.NewGuid();
        await store.TryCreateGenesisAsync(id, "touch@example.com", "original-hash", CancellationToken.None);

        await store.TouchLastSignInAsync(id, CancellationToken.None);

        using var checkCmd = new NpgsqlCommand(
            "SELECT last_sign_in_at_utc IS NOT NULL, password_hash, email FROM platform_admins WHERE id = $1", owner);
        checkCmd.Parameters.AddWithValue(id);
        using var reader = checkCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("original-hash", reader.GetString(1));
        Assert.Equal("touch@example.com", reader.GetString(2));
    }

    [Fact]
    public async Task ListOrganizationsAsync_ViaPlatformReadonlyDataSource_ReturnsSeededOrganizations()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        ApplyAllMigrationsAndReset(owner);

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        using (var insertCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, 'Org A'), ($2, 'Org B')", owner))
        {
            insertCmd.Parameters.AddWithValue(orgAId);
            insertCmd.Parameters.AddWithValue(orgBId);
            insertCmd.ExecuteNonQuery();
        }

        var store = new PostgresPlatformAdminStore(
            NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString),
            NpgsqlDataSource.Create(MigrationRlsTests.PlatformReadonlyConnectionString));

        Assert.True(store.CanListOrganizations);
        var organizations = await store.ListOrganizationsAsync(CancellationToken.None);

        Assert.Contains(organizations, o => o.Id == orgAId && o.Name == "Org A");
        Assert.Contains(organizations, o => o.Id == orgBId && o.Name == "Org B");
    }

    [Fact]
    public void CanListOrganizations_IsFalse_WhenNoPlatformReadDataSourceProvided()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPlatformAdminStore(NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString));
        Assert.False(store.CanListOrganizations);
    }
}
