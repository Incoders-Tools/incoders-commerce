using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Tenancy;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// organization-persistence "Branch Short Code" against the real store:
/// bootstrap gets code 1, later branches are sequential per organization,
/// organizations number independently, and concurrent creations for one
/// organization never collide.
/// </summary>
[Collection("Postgres")]
public sealed class BranchCodeStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public BranchCodeStoreTests()
    {
        if (!_postgresAvailable) return;
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        foreach (var file in new[]
                 {
                     "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql",
                     "0004_device_credentials.sql", "0005_password_recovery.sql", "0012_admin_console.sql",
                     "0021_branch_codes.sql",
                 })
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private async Task<(PostgresOrganizationStore Store, CloudTenantScope Scope, Guid BranchId)> BootstrapAsync(string email)
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var store = new PostgresOrganizationStore(_dataSource!, userStore);
        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var outcome = await store.TryCreateBootstrapAsync(
            scope, new NewOrganization(orgId, "Org " + orgId), new NewBranch(branchId, "Main"),
            new NewUserAccount(Guid.NewGuid(), email, "hashed", [branchId], [new RoleDto("admin", Permission.ManageCatalog)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);
        return (store, scope, branchId);
    }

    [Fact]
    public async Task Bootstrap_AssignsCodeOneToTheDefaultBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (store, scope, branchId) = await BootstrapAsync($"code-one-{Guid.NewGuid():N}@example.com");

        var branch = Assert.Single(await store.ListBranchesAsync(scope, CancellationToken.None));
        Assert.Equal(branchId, branch.Id);
        Assert.Equal(1, branch.Code);
    }

    [Fact]
    public async Task CreateBranch_ReturnsSequentialCodes_AndOrganizationsNumberIndependently()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (storeA, scopeA, _) = await BootstrapAsync($"seq-a-{Guid.NewGuid():N}@example.com");
        var (storeB, scopeB, _) = await BootstrapAsync($"seq-b-{Guid.NewGuid():N}@example.com");

        var second = await storeA.CreateBranchAsync(scopeA, new NewBranch(Guid.NewGuid(), "Segunda"), CancellationToken.None);
        var third = await storeA.CreateBranchAsync(scopeA, new NewBranch(Guid.NewGuid(), "Tercera"), CancellationToken.None);
        var otherOrgSecond = await storeB.CreateBranchAsync(scopeB, new NewBranch(Guid.NewGuid(), "Segunda"), CancellationToken.None);

        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.Equal(2, otherOrgSecond);
        Assert.Equal(new[] { 1, 2, 3 }, (await storeA.ListBranchesAsync(scopeA, CancellationToken.None)).Select(b => b.Code).Order());
        Assert.Equal(new[] { 1, 2 }, (await storeB.ListBranchesAsync(scopeB, CancellationToken.None)).Select(b => b.Code).Order());
    }

    [Fact]
    public async Task ConcurrentCreateBranch_ForOneOrganization_ProducesDistinctSequentialCodes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (store, scope, _) = await BootstrapAsync($"race-{Guid.NewGuid():N}@example.com");

        var codes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Task.Run(() => store.CreateBranchAsync(scope, new NewBranch(Guid.NewGuid(), "Racing " + i), CancellationToken.None))));

        Assert.Equal(Enumerable.Range(2, 12), codes.Order());
        var persisted = (await store.ListBranchesAsync(scope, CancellationToken.None)).Select(b => b.Code).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 13), persisted);
    }

    [Fact]
    public async Task CreateBranch_WhenTheOrganizationHoldsCode999_ThrowsTheTypedExhaustionError_AndWritesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (store, scope, _) = await BootstrapAsync($"exhausted-{Guid.NewGuid():N}@example.com");
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var top = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name, code) VALUES ($1, $2, 'Last', 999)", owner);
            top.Parameters.AddWithValue(Guid.NewGuid()); top.Parameters.AddWithValue(scope.OrganizationId); top.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<BranchCodesExhaustedException>(() =>
            store.CreateBranchAsync(scope, new NewBranch(Guid.NewGuid(), "Overflow"), CancellationToken.None));

        Assert.DoesNotContain(await store.ListBranchesAsync(scope, CancellationToken.None), b => b.Name == "Overflow");
    }
}
