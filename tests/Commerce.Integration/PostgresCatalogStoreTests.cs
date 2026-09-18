using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 1.4 (RED+GREEN): real Npgsql-backed
/// catalog persistence — CRUD for `products`/`presentations`,
/// `FindByIdentificationCodeAsync`, `ListChangedSinceAsync`, and the
/// `presentations_org_code_uk` partial unique index (duplicate code within
/// an org rejected, permitted across orgs). If Postgres is not reachable,
/// these tests report the gap clearly and return without asserting
/// pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresCatalogStoreTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public PostgresCatalogStoreTests()
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

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE presentations, products, branches, organizations CASCADE", owner);
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

    [Fact]
    public async Task CreateAndFindProduct_RoundTrips()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var created = await store.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Yerba Mate 1kg", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var found = await store.FindProductAsync(scope, created.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Yerba Mate 1kg", found!.Name);
        Assert.Equal(organizationId, found.OrganizationId);
    }

    [Fact]
    public async Task UpdateProduct_ForCrossOrgTarget_ReturnsNull_IdenticalToNonexistent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId);
        SeedOrganization(orgBId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var created = await store.CreateProductAsync(
            new CloudTenantScope(orgAId), new NewProduct(Guid.NewGuid(), "Org A Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var updated = await store.UpdateProductAsync(
            new CloudTenantScope(orgBId), created.Id, new UpdateProduct("Rogue Name", Guid.NewGuid(), Guid.NewGuid()),
            "org-user", actorId, CancellationToken.None);

        Assert.Null(updated);
    }

    [Fact]
    public async Task CreatePresentation_WithIdentificationCode_IsFoundByCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var product = await store.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Yerba Mate", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var presentation = await store.CreatePresentationAsync(
            scope,
            new NewPresentation(Guid.NewGuid(), product.Id, "1kg bag", QuantityBehavior.FixedQuantity, Guid.NewGuid(), "7791234567890", actorId),
            "org-user", actorId, CancellationToken.None);

        var found = await store.FindByIdentificationCodeAsync(scope, "7791234567890", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(presentation.Id, found!.Id);
    }

    /// <summary>
    /// `presentations_org_code_uk`: a duplicate `identification_code` WITHIN
    /// the same organization is rejected (design.md "Identification code
    /// placement and uniqueness").
    /// </summary>
    [Fact]
    public async Task CreatePresentation_DuplicateCodeWithinSameOrganization_Throws()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var product = await store.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product A", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        await store.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Presentation 1", QuantityBehavior.FixedQuantity, Guid.NewGuid(), "SAME-CODE", actorId),
            "org-user", actorId, CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() => store.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "Presentation 2", QuantityBehavior.FixedQuantity, Guid.NewGuid(), "SAME-CODE", actorId),
            "org-user", actorId, CancellationToken.None));
    }

    /// <summary>
    /// The same code IS permitted across two different organizations — two
    /// orgs may legitimately share an EAN-13 (design.md, same section).
    /// </summary>
    [Fact]
    public async Task CreatePresentation_SameCodeAcrossDifferentOrganizations_Succeeds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        SeedOrganization(orgAId);
        SeedOrganization(orgBId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var productA = await store.CreateProductAsync(
            new CloudTenantScope(orgAId), new NewProduct(Guid.NewGuid(), "Product A", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);
        var productB = await store.CreateProductAsync(
            new CloudTenantScope(orgBId), new NewProduct(Guid.NewGuid(), "Product B", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        await store.CreatePresentationAsync(
            new CloudTenantScope(orgAId), new NewPresentation(Guid.NewGuid(), productA.Id, "Presentation A", QuantityBehavior.FixedQuantity, Guid.NewGuid(), "SHARED-EAN", actorId),
            "org-user", actorId, CancellationToken.None);

        var createdB = await store.CreatePresentationAsync(
            new CloudTenantScope(orgBId), new NewPresentation(Guid.NewGuid(), productB.Id, "Presentation B", QuantityBehavior.FixedQuantity, Guid.NewGuid(), "SHARED-EAN", actorId),
            "org-user", actorId, CancellationToken.None);

        Assert.NotNull(createdB);
        Assert.Equal("SHARED-EAN", createdB.IdentificationCode);
    }

    [Fact]
    public async Task ListChangedSinceAsync_ReturnsOnlyPresentationsUpdatedAfterCursor()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        SeedOrganization(organizationId);
        var scope = new CloudTenantScope(organizationId);
        var store = new PostgresCatalogStore(_dataSource!);
        var actorId = Guid.NewGuid();

        var product = await store.CreateProductAsync(
            scope, new NewProduct(Guid.NewGuid(), "Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var cursor = DateTimeOffset.UtcNow;
        await Task.Delay(50);

        var presentation = await store.CreatePresentationAsync(
            scope, new NewPresentation(Guid.NewGuid(), product.Id, "New Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        var changed = await store.ListChangedSinceAsync(scope, cursor, CancellationToken.None);

        Assert.Contains(changed, row => row.PresentationId == presentation.Id);
    }
}
