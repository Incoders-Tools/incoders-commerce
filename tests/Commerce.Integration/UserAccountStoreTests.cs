using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials task 2.1: `PostgresUserAccountStore`
/// against a LIVE Postgres instance (`deploy/dev/compose.yaml`), connecting
/// as the non-owner `app_runtime` role. If compose is not running, these
/// tests report the gap clearly and return without asserting pass/fail — see
/// `PostgresTestFixture`.
/// </summary>
[Collection("Postgres")]
public sealed class UserAccountStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public UserAccountStoreTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Commerce.sln")))
        {
            repoRoot = repoRoot.Parent;
        }
        if (repoRoot is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }

        var initSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        using var resetCmd = new NpgsqlCommand("TRUNCATE TABLE user_directory, users", owner);
        resetCmd.ExecuteNonQuery();
    }

    private static NewUserAccount NewUser(Guid id, string email, IReadOnlyList<Guid>? branchScope = null) =>
        new(id, email, "hashed-password", branchScope ?? Array.Empty<Guid>(),
            new[] { new RoleDto("admin", Permission.ManageCatalog) });

    [Fact]
    public async Task FindDirectoryEntryAsync_IsUnscoped_ResolvesEmailToOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var organizationId = Guid.NewGuid();
        var scope = new CloudTenantScope(organizationId);
        var userId = Guid.NewGuid();

        var created = await store.TryCreateAsync(scope, NewUser(userId, "unscoped@example.com"), CancellationToken.None);
        Assert.True(created);

        var entry = await store.FindDirectoryEntryAsync("unscoped@example.com", CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(organizationId, entry!.OrganizationId);
        Assert.Equal(userId, entry.UserId);
    }

    [Fact]
    public async Task FindDirectoryEntryAsync_UnknownEmail_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);

        var entry = await store.FindDirectoryEntryAsync("nobody@example.com", CancellationToken.None);

        Assert.Null(entry);
    }

    [Fact]
    public async Task FindByEmailAsync_And_LoadActorAsync_NeverExposeAnotherOrganizationsRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var orgAScope = new CloudTenantScope(Guid.NewGuid());
        var orgBScope = new CloudTenantScope(Guid.NewGuid());
        var userId = Guid.NewGuid();
        await store.TryCreateAsync(orgAScope, NewUser(userId, "crossorg@example.com"), CancellationToken.None);

        var crossOrgByEmail = await store.FindByEmailAsync(orgBScope, "crossorg@example.com", CancellationToken.None);
        var crossOrgActor = await store.LoadActorAsync(orgBScope, userId, CancellationToken.None);

        Assert.Null(crossOrgByEmail);
        Assert.Null(crossOrgActor);

        var ownOrgByEmail = await store.FindByEmailAsync(orgAScope, "crossorg@example.com", CancellationToken.None);
        Assert.NotNull(ownOrgByEmail);
    }

    [Fact]
    public async Task LoadActorAsync_MapsIsRevoked_ToUserAccountIsRevoked()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var scope = new CloudTenantScope(Guid.NewGuid());
        var userId = Guid.NewGuid();
        await store.TryCreateAsync(scope, NewUser(userId, "revoked@example.com"), CancellationToken.None);

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var cmd = new NpgsqlCommand("UPDATE users SET is_revoked = true WHERE id = $1", owner);
            cmd.Parameters.AddWithValue(userId);
            cmd.ExecuteNonQuery();
        }

        var actor = await store.LoadActorAsync(scope, userId, CancellationToken.None);

        Assert.NotNull(actor);
        Assert.True(actor!.IsRevoked);
    }

    [Fact]
    public async Task LoadActorAsync_RoundTripsRolesAndBranchScope()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var scope = new CloudTenantScope(Guid.NewGuid());
        var userId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await store.TryCreateAsync(
            scope,
            NewUser(userId, "roles@example.com", new[] { branchId }),
            CancellationToken.None);

        var actor = await store.LoadActorAsync(scope, userId, CancellationToken.None);

        Assert.NotNull(actor);
        Assert.Contains(branchId, actor!.BranchScope);
        var role = Assert.Single(actor.Roles);
        Assert.Equal("admin", role.Name);
        Assert.Equal(Permission.ManageCatalog, role.Permissions);
    }

    [Fact]
    public async Task HasAnyUserAsync_ReflectsOrgScopedExistence()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var emptyOrgScope = new CloudTenantScope(Guid.NewGuid());
        var populatedOrgScope = new CloudTenantScope(Guid.NewGuid());
        await store.TryCreateAsync(populatedOrgScope, NewUser(Guid.NewGuid(), "hasany@example.com"), CancellationToken.None);

        Assert.False(await store.HasAnyUserAsync(emptyOrgScope, CancellationToken.None));
        Assert.True(await store.HasAnyUserAsync(populatedOrgScope, CancellationToken.None));
    }

    [Fact]
    public async Task TryCreateAsync_DuplicateEmail_ReturnsFalse_NotException()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresUserAccountStore(_dataSource!);
        var scope = new CloudTenantScope(Guid.NewGuid());
        var first = await store.TryCreateAsync(scope, NewUser(Guid.NewGuid(), "dup@example.com"), CancellationToken.None);
        var second = await store.TryCreateAsync(scope, NewUser(Guid.NewGuid(), "dup@example.com"), CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
    }
}
