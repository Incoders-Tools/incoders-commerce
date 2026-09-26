using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-organization-persistence tasks 2.6-2.9:
/// `PostgresOrganizationStore` against a LIVE Postgres instance
/// (`deploy/dev/compose.yaml`). If Postgres is not reachable, these tests
/// report the gap clearly and return without asserting pass/fail, matching
/// the existing fixture convention (`PostgresTestFixture`).
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public OrganizationStoreTests()
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

        var repoRoot = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var orgsSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(orgsSql, owner)) cmd.ExecuteNonQuery();

        var deviceSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0004_device_credentials.sql"));
        using (var cmd = new NpgsqlCommand(deviceSql, owner)) cmd.ExecuteNonQuery();

        var recoverySql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0005_password_recovery.sql"));
        using (var cmd = new NpgsqlCommand(recoverySql, owner)) cmd.ExecuteNonQuery();

        // B1 (frontend-modernization): adds `users.is_system_admin`, needed
        // by this file's own PromoteToSystemAdminAsync coverage below. Its
        // platform_admins-merge block is guarded by
        // `to_regclass(...) IS NOT NULL`, so applying it here without
        // 0006-0011 first is a no-op there.
        var adminConsoleSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0012_admin_console.sql"));
        using (var cmd = new NpgsqlCommand(adminConsoleSql, owner)) cmd.ExecuteNonQuery();

        // device_credentials (0004) and password_reset_tokens (0005) carry
        // FKs to organizations/branches, so they must be truncated
        // before/alongside them. CASCADE additionally covers `customers`
        // (0008), which may already exist in this shared database from
        // another test class in the same run even though this class never
        // applies 0008 itself.
        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private static NewUserAccount NewAdmin(Guid id, string email, IReadOnlyList<Guid>? branchScope = null) =>
        new(id, email, "hashed-password", branchScope ?? Array.Empty<Guid>(),
            new[] { new RoleDto("admin", Permission.ManageCatalog) });

    private static (long Orgs, long Branches, long Users, long Directory) CountAllRows()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        long Count(string table)
        {
            using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", owner);
            return (long)cmd.ExecuteScalar()!;
        }

        return (Count("organizations"), Count("branches"), Count("users"), Count("user_directory"));
    }

    [Fact]
    public async Task TryCreateBootstrapAsync_HappyPath_CreatesOrganizationBranchAndAdmin_SharingOneOrganizationId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);

        var outcome = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Acme Corp"),
            new NewBranch(branchId, "Main"),
            NewAdmin(userId, "happy@example.com", [branchId]),
            CancellationToken.None);

        Assert.Equal(BootstrapOutcome.Created, outcome);

        var actor = await userStore.LoadActorAsync(scope, userId, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.Equal(orgId, actor!.OrganizationId);
        Assert.Contains(branchId, actor.BranchScope);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT organization_id, name FROM branches WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(branchId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(orgId, reader.GetGuid(0));
        Assert.Equal("Main", reader.GetString(1));
    }

    /// <summary>
    /// organization-persistence spec: "Branch created with a caller-supplied
    /// name" — the happy-path test above only ever exercises the default
    /// "Main" branch name, so it never proves a caller-supplied, non-default
    /// name actually persists (a hardcoded default could pass that test even
    /// if the real `branchName` field were silently ignored). This test uses
    /// a distinct name to close that gap.
    /// </summary>
    [Fact]
    public async Task TryCreateBootstrapAsync_CallerSuppliedBranchName_PersistsExactly()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        const string callerSuppliedName = "Downtown Branch";

        var outcome = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Named Branch Co"),
            new NewBranch(branchId, callerSuppliedName),
            NewAdmin(Guid.NewGuid(), "named-branch@example.com", [branchId]),
            CancellationToken.None);

        Assert.Equal(BootstrapOutcome.Created, outcome);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT name FROM branches WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(branchId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(callerSuppliedName, reader.GetString(0));
        Assert.NotEqual("Main", reader.GetString(0));
    }

    [Fact]
    public async Task TryCreateBootstrapAsync_OrganizationAlreadyExists_RollsBack_ZeroNewRows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);

        var first = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "First Org"),
            new NewBranch(Guid.NewGuid(), "Main"),
            NewAdmin(Guid.NewGuid(), "first@example.com"),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, first);

        var before = CountAllRows();

        var second = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Duplicate Org"),
            new NewBranch(Guid.NewGuid(), "Secondary"),
            NewAdmin(Guid.NewGuid(), "second@example.com"),
            CancellationToken.None);

        Assert.Equal(BootstrapOutcome.OrganizationAlreadyExists, second);
        Assert.Equal(before, CountAllRows());
    }

    [Fact]
    public async Task TryCreateBootstrapAsync_OrganizationAlreadyHasUsers_RollsBack_ZeroNewRows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        // Seed a user directly (a different org than the one under test) so
        // "any user exists" is true globally is NOT what's being tested —
        // instead this proves the SAME org's re-check: insert a user for
        // orgId via the plain user store first (simulating a prior partial
        // history), then attempt bootstrap for that same org.
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        await userStore.TryCreateAsync(scope, NewAdmin(Guid.NewGuid(), "existing@example.com"), CancellationToken.None);

        var before = CountAllRows();

        var outcome = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Org With Users"),
            new NewBranch(Guid.NewGuid(), "Main"),
            NewAdmin(Guid.NewGuid(), "new-admin@example.com"),
            CancellationToken.None);

        Assert.Equal(BootstrapOutcome.OrganizationAlreadyHasUsers, outcome);
        Assert.Equal(before, CountAllRows());
    }

    /// <summary>
    /// LOAD-BEARING ATOMICITY TEST (design.md "the critical test"). Forces
    /// the failure at the FINAL admin-insert step: organization+branch rows
    /// have ALREADY been inserted in this transaction when the admin insert
    /// throws (a `user_directory` email already registered to a DIFFERENT
    /// organization triggers a unique-violation on `user_directory`'s
    /// primary key). Asserts NO orphaned organization/branch row remains
    /// after rollback — a shallow test exercising only an early guard
    /// (org-already-exists, zero-users) would pass even without real
    /// transactional rollback and would prove nothing.
    /// </summary>
    [Fact]
    public async Task TryCreateBootstrapAsync_AdminInsertFailsAfterOrgAndBranchAlreadyInserted_RollsBackEverything()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        // Register the email to a DIFFERENT organization first, so the new
        // bootstrap's admin insert collides on user_directory's
        // email_normalized primary key — but only AFTER this transaction's
        // own organizations+branches inserts have already run.
        var otherOrgId = Guid.NewGuid();
        var collidingEmail = "collision@example.com";
        var otherOrgCreated = await userStore.TryCreateAsync(
            new CloudTenantScope(otherOrgId),
            NewAdmin(Guid.NewGuid(), collidingEmail),
            CancellationToken.None);
        Assert.True(otherOrgCreated);

        var before = CountAllRows();

        var newOrgId = Guid.NewGuid();
        var newBranchId = Guid.NewGuid();
        var scope = new CloudTenantScope(newOrgId);

        var outcome = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(newOrgId, "Colliding Org"),
            new NewBranch(newBranchId, "Main"),
            NewAdmin(Guid.NewGuid(), collidingEmail),
            CancellationToken.None);

        Assert.Equal(BootstrapOutcome.EmailAlreadyRegistered, outcome);

        // The critical assertion: zero rows changed anywhere, including
        // organizations/branches which WERE inserted before the failure.
        Assert.Equal(before, CountAllRows());

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var orgCmd = new NpgsqlCommand("SELECT count(*) FROM organizations WHERE id = $1", owner);
        orgCmd.Parameters.AddWithValue(newOrgId);
        Assert.Equal(0L, (long)orgCmd.ExecuteScalar()!);

        using var branchCmd = new NpgsqlCommand("SELECT count(*) FROM branches WHERE id = $1", owner);
        branchCmd.Parameters.AddWithValue(newBranchId);
        Assert.Equal(0L, (long)branchCmd.ExecuteScalar()!);
    }

    /// <summary>
    /// B1 (odd/tasks/frontend-modernization.md, product review backlog):
    /// the platform sysadmin must never carry an organization role — the bug
    /// was `TestSeedEndpoints` always granting `business-admin`. This proves
    /// the chosen shape end to end: bootstrap a user with ZERO roles and
    /// ZERO branch scope (as the seed seam's `systemAdmin` branch does), then
    /// promote it, and assert the promotion sets ONLY `is_system_admin` —
    /// roles and branch_scope are untouched, still empty.
    /// </summary>
    [Fact]
    public async Task PromoteToSystemAdminAsync_SetsFlagOnly_RolesAndBranchScopeStayEmpty()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);

        var outcome = await store.TryCreateBootstrapAsync(
            scope,
            new NewOrganization(orgId, "Platform System Administrator"),
            new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, "sysadmin@example.com", "hashed-password", Array.Empty<Guid>(), Array.Empty<RoleDto>()),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);

        var beforePromotion = await userStore.LoadActorAsync(scope, userId, CancellationToken.None);
        Assert.NotNull(beforePromotion);
        Assert.False(beforePromotion!.IsSystemAdmin);

        await userStore.PromoteToSystemAdminAsync(scope, userId, CancellationToken.None);

        var actor = await userStore.LoadActorAsync(scope, userId, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.True(actor!.IsSystemAdmin);
        Assert.Empty(actor.Roles);
        Assert.Empty(actor.BranchScope);
        Assert.Equal(Permission.None, actor.EffectivePermissions);

        using var ownerConn = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT is_system_admin, roles, branch_scope FROM users WHERE id = $1", ownerConn);
        cmd.Parameters.AddWithValue(userId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("[]", reader.GetString(1));
        Assert.Empty(reader.GetFieldValue<Guid[]>(2));
    }
}
