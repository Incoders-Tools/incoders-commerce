using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 2.4 (GREEN) and 2.5 (RED+GREEN):
/// `PostgresPriceListStore` — append, effective-date resolution, history,
/// and the one-default-per-organization constraint. If Postgres is not
/// reachable, these tests report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresPriceListStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PostgresPriceListStoreTests()
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

        var repoRoot = RepoRoot();

        void Apply(string file, string? placeholder = null, string? replacement = null)
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", file));
            if (placeholder is not null)
            {
                sql = sql.Replace(placeholder, replacement);
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        Apply("0001_init_rls.sql", "__APP_RUNTIME_PASSWORD__", "dev-only-password");
        Apply("0002_users.sql");
        Apply("0003_organizations_branches.sql");
        Apply("0009_catalog_and_pricing.sql");
        Apply("0016_catalog_branch_ownership.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_list_entries, price_lists, presentations, products, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private static void SeedOrganization(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>B7 U4: catalog scopes now need a real branch row.</summary>
    private static Guid SeedBranch(Guid organizationId)
    {
        var branchId = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", owner);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.ExecuteNonQuery();
        return branchId;
    }

    private async Task<(CloudTenantScope Scope, Guid PresentationId, Guid ActorId)> SeedPresentationAsync(Guid organizationId)
    {
        var branchId = SeedBranch(organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var catalogStore = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var product = await catalogStore.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var presentation = await catalogStore.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        return (scope, presentation.Id, actorId);
    }

    [Fact]
    public async Task AppendEntryAsync_ThenGetEffectiveAsync_ResolvesLatestEntryOnOrBeforeDate()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var (scope, presentationId, actorId) = await SeedPresentationAsync(organizationId);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 100.00m, new DateOnly(2026, 1, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 150.00m, new DateOnly(2026, 2, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        // Resolving BEFORE the second publish's effective date must still
        // return the FIRST price — the prior entry stays retrievable.
        var beforeSecond = await priceStore.GetEffectiveAsync(scope, priceList.Id, presentationId, new DateOnly(2026, 1, 15), CancellationToken.None);
        Assert.NotNull(beforeSecond);
        Assert.Equal(100.00m, beforeSecond!.UnitPrice);

        var afterSecond = await priceStore.GetEffectiveAsync(scope, priceList.Id, presentationId, new DateOnly(2026, 3, 1), CancellationToken.None);
        Assert.NotNull(afterSecond);
        Assert.Equal(150.00m, afterSecond!.UnitPrice);
    }

    /// <summary>
    /// "No effective price for this date" is exactly ZERO matching rows —
    /// asserted here as a null return, never a zero/default substitution
    /// (design.md "Effective-dating shape").
    /// </summary>
    [Fact]
    public async Task GetEffectiveAsync_BeforeEarliestEntry_ReturnsNull_NeverZero()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var (scope, presentationId, actorId) = await SeedPresentationAsync(organizationId);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 100.00m, new DateOnly(2026, 6, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var beforeEarliest = await priceStore.GetEffectiveAsync(scope, priceList.Id, presentationId, new DateOnly(2026, 1, 1), CancellationToken.None);

        Assert.Null(beforeEarliest);
    }

    [Fact]
    public async Task GetEffectiveAsync_ForPresentationWithZeroEntries_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var (scope, presentationId, actorId) = await SeedPresentationAsync(organizationId);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        var result = await priceStore.GetEffectiveAsync(scope, priceList.Id, presentationId, DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ListHistoryAsync_ReturnsAllEntriesNewestFirst()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var (scope, presentationId, actorId) = await SeedPresentationAsync(organizationId);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 100.00m, new DateOnly(2026, 1, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);
        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 150.00m, new DateOnly(2026, 2, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        var history = await priceStore.ListHistoryAsync(scope, priceList.Id, presentationId, CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal(150.00m, history[0].UnitPrice);
        Assert.Equal(100.00m, history[1].UnitPrice);
    }

    /// <summary>A same-day double-publish surfaces the DB constraint as a thrown exception (409 at the endpoint layer).</summary>
    [Fact]
    public async Task AppendEntryAsync_SameDayDoublePublish_Throws()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var (scope, presentationId, actorId) = await SeedPresentationAsync(organizationId);

        var priceStore = new PostgresPriceListStore(_dataSource!);
        var priceList = await priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        await priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 100.00m, new DateOnly(2026, 1, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() => priceStore.AppendEntryAsync(
            scope, new NewPriceListEntry(Guid.NewGuid(), priceList.Id, presentationId, 999.00m, new DateOnly(2026, 1, 1), "Manual", null, actorId),
            "org-user", actorId, CancellationToken.None));
    }

    /// <summary>One default price list per organization — a second `CreatePriceListAsync(isDefault: true)` throws.</summary>
    [Fact]
    public async Task CreatePriceListAsync_SecondDefaultForSameOrganization_Throws()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceStore = new PostgresPriceListStore(_dataSource!);

        await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() => priceStore.CreatePriceListAsync(
            scope, new NewPriceList(Guid.NewGuid(), "Also Default", true, actorId), "org-user", actorId, CancellationToken.None));
    }

    [Fact]
    public async Task FindDefaultPriceListAsync_ReturnsTheOrganizationsDefaultList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();
        var priceStore = new PostgresPriceListStore(_dataSource!);

        await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), "Secondary", false, actorId), "org-user", actorId, CancellationToken.None);
        var created = await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), "Default", true, actorId), "org-user", actorId, CancellationToken.None);

        var found = await priceStore.FindDefaultPriceListAsync(scope, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }
}
