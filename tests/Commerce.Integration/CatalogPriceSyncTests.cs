using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 6 task 6.3/6.4: `GET
/// /device/catalog/sync` — device bearer required, org comes from the STORED
/// `device_credentials` row (never the query), `since` filters to changed
/// rows only, and the response combines the catalog projection with its
/// currently-effective price in ONE row (design.md "BranchNode replication:
/// one channel, not two"). Also the process-integration threat-matrix row: an
/// unreachable host or a 401 must leave nothing to apply. Mirrors
/// <see cref="CustomerSyncTests"/> exactly. If Postgres is not reachable,
/// these tests report the gap clearly and return without asserting
/// pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class CatalogPriceSyncTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogPriceSyncTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
        });

        if (_postgresAvailable)
        {
            ApplyMigrationsAndReset();
        }
    }

    public void Dispose() => _factory.Dispose();

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
        Apply("0004_device_credentials.sql");
        Apply("0009_catalog_and_pricing.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_list_entries, price_lists, presentations, products, " +
            "user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", connection);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedBranchAsync(Guid orgId, Guid branchId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", connection);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> IssueDeviceTokenAsync(Guid orgId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return issued.PlaintextToken;
    }

    private async Task<Guid> SeedPresentationAsync(Guid organizationId, string? identificationCode = "7791234500000")
    {
        using var scope = _factory.Services.CreateScope();
        var catalogStore = scope.ServiceProvider.GetRequiredService<PostgresCatalogStore>();
        var tenantScope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var product = await catalogStore.CreateProductAsync(
            tenantScope, new NewProduct(Guid.NewGuid(), "Flour", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var presentation = await catalogStore.CreatePresentationAsync(
            tenantScope,
            new NewPresentation(Guid.NewGuid(), product.Id, "1kg Bag", QuantityBehavior.FixedQuantity, Guid.NewGuid(), identificationCode, actorId),
            "org-user", actorId, CancellationToken.None);

        return presentation.Id;
    }

    private async Task<Guid> SeedDefaultPriceListAsync(Guid organizationId)
    {
        using var scope = _factory.Services.CreateScope();
        var priceListStore = scope.ServiceProvider.GetRequiredService<PostgresPriceListStore>();
        var tenantScope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var priceList = await priceListStore.CreatePriceListAsync(
            tenantScope, new NewPriceList(Guid.NewGuid(), "Default", IsDefault: true, actorId),
            "org-user", actorId, CancellationToken.None);

        return priceList.Id;
    }

    private async Task PublishPriceAsync(Guid organizationId, Guid priceListId, Guid presentationId, decimal unitPrice)
    {
        using var scope = _factory.Services.CreateScope();
        var priceListStore = scope.ServiceProvider.GetRequiredService<PostgresPriceListStore>();
        var tenantScope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        await priceListStore.AppendEntryAsync(
            tenantScope,
            new NewPriceListEntry(
                Guid.NewGuid(), priceListId, presentationId, unitPrice, DateOnly.FromDateTime(DateTime.UtcNow),
                "Manual", ImportBatchId: null, actorId),
            "org-user", actorId, CancellationToken.None);
    }

    private static HttpRequestMessage BuildRequest(string path, string? deviceToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (deviceToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        }
        return request;
    }

    [Fact]
    public async Task Sync_NoDeviceBearer_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_ValidDeviceBearer_ReturnsCatalogRowWithCurrentPrice_ScopedToStoredOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var presentationId = await SeedPresentationAsync(orgId);
        var priceListId = await SeedDefaultPriceListAsync(orgId);
        await PublishPriceAsync(orgId, priceListId, presentationId, 42.50m);

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(since.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CatalogSyncResponse>();
        var item = Assert.Single(body!.Items, i => i.PresentationId == presentationId);
        Assert.Equal(42.50m, item.UnitPrice);
        Assert.Equal("7791234500000", item.IdentificationCode);
    }

    [Fact]
    public async Task Sync_SinceCursorAfterCreationAndPublish_ExcludesUnchangedPresentation()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var presentationId = await SeedPresentationAsync(orgId);
        var priceListId = await SeedDefaultPriceListAsync(orgId);
        await PublishPriceAsync(orgId, priceListId, presentationId, 10m);
        await Task.Delay(50);
        var cursor = DateTimeOffset.UtcNow;

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(cursor.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CatalogSyncResponse>();
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task Sync_PriceOnlyChange_AppearsWithFullCatalogProjection()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var presentationId = await SeedPresentationAsync(orgId);
        var priceListId = await SeedDefaultPriceListAsync(orgId);
        await Task.Delay(50);
        var cursor = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        // Only the price changes after the cursor; the catalog row does not.
        await PublishPriceAsync(orgId, priceListId, presentationId, 77m);

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(cursor.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CatalogSyncResponse>();
        var item = Assert.Single(body!.Items, i => i.PresentationId == presentationId);
        Assert.Equal(77m, item.UnitPrice);
        Assert.Equal("1kg Bag", item.PresentationName);
    }

    [Fact]
    public async Task Sync_NoEffectivePrice_ReturnsNullUnitPrice_NeverZero()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var presentationId = await SeedPresentationAsync(orgId);
        // No price list, no published price at all.

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(since.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CatalogSyncResponse>();
        var item = Assert.Single(body!.Items, i => i.PresentationId == presentationId);
        Assert.Null(item.UnitPrice);
        Assert.Null(item.EffectiveFrom);
    }

    [Fact]
    public async Task Sync_OrgAToken_NeverSeesOrgBPresentations()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        await SeedBranchAsync(orgAId, branchAId);
        var deviceTokenA = await IssueDeviceTokenAsync(orgAId, branchAId);

        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedPresentationAsync(orgBId, identificationCode: "ORG-B-ONLY");

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/catalog/sync?since={Uri.EscapeDataString(since.ToString("O"))}", deviceTokenA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CatalogSyncResponse>();
        Assert.Empty(body!.Items);
    }
}
