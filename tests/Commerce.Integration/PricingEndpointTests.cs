using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine task 5.1/5.3/5.4: <c>Endpoints/Pricing.cs</c>
/// — price list CRUD, appending an effective-dated entry, and history —
/// authorized with <c>CustomerEndpoints</c>/<c>Account.cs</c>'s exact
/// <c>adminGroup</c> shape, gated on <see cref="Permission.ManageCatalog"/>
/// (corrected from design.md's original literal "ManageUsers" — publishing
/// a price is a catalog/commercial-pricing concern, not a user-management
/// one). Threat-matrix Routing row: a
/// caller lacking the permission gets 403, a cookie-less caller gets 401, a
/// device-bearer caller gets 401, and a cross-organization price list is
/// invisible under RLS (404, identical to nonexistent). If Postgres is not
/// reachable, these tests report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class PricingEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PricingEndpointTests(WebApplicationFactory<Program> factory)
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
        Apply("0016_catalog_branch_ownership.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_list_entries, price_lists, presentations, products, " +
            "user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>B7 U4: catalog scopes now need a real branch row.</summary>
    private async Task<Guid> SeedBranchAsync(Guid organizationId)
    {
        var branchId = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", owner);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(organizationId);
        await cmd.ExecuteNonQueryAsync();
        return branchId;
    }

    private async Task<Guid> SeedPresentationAsync(Guid organizationId)
    {
        using var scope = _factory.Services.CreateScope();
        var catalogStore = scope.ServiceProvider.GetRequiredService<PostgresCatalogStore>();
        var branchId = await SeedBranchAsync(organizationId);
        var tenantScope = new CloudTenantScope(organizationId, BranchId: branchId);
        var actorId = Guid.NewGuid();

        var product = await catalogStore.CreateProductAsync(
            tenantScope, new NewProduct(Guid.NewGuid(), "Seed Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var presentation = await catalogStore.CreatePresentationAsync(
            tenantScope,
            new NewPresentation(Guid.NewGuid(), product.Id, "Seed Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), null, actorId),
            "org-user", actorId, CancellationToken.None);

        return presentation.Id;
    }

    /// <summary>
    /// Mirrors <c>CatalogEndpointTests.SignedInClientAsync</c>: real
    /// cookie-issuing sign-in so the endpoint's authorization decision is
    /// driven only by what is persisted for the user.
    /// </summary>
    private async Task<(HttpClient client, Guid organizationId)> SignedInClientAsync(
        Permission permissions, Guid? organizationId = null)
    {
        var isNewOrganization = organizationId is null;
        organizationId ??= Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        if (isNewOrganization)
        {
            await SeedOrganizationAsync(organizationId.Value);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var newUser = new NewUserAccount(
                userId, $"{userId}@example.com", "unused-hash", new[] { branchId }, new[] { new RoleDto("test-role", permissions) });
            var tenantScope = new CloudTenantScope(organizationId.Value);

            if (isNewOrganization)
            {
                var created = await store.TryCreateAsync(tenantScope, newUser, CancellationToken.None);
                Assert.True(created);
            }
            else
            {
                var outcome = await store.CreateStaffUserAsync(
                    tenantScope, newUser,
                    new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry(
                        "org-user", Guid.NewGuid(), organizationId.Value, "user", userId, "user.created", null, null),
                    CancellationToken.None);
                Assert.Equal(CreateStaffUserOutcome.Created, outcome);
            }
        }

        return (await SignInViaTestEndpointAsync(organizationId.Value, userId), organizationId.Value);
    }

    private async Task<HttpClient> SignInViaTestEndpointAsync(Guid organizationId, Guid userId)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();

            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), "test-password");
            using var cmd = new NpgsqlCommand("UPDATE users SET password_hash = $1 WHERE id = $2", owner);
            cmd.Parameters.AddWithValue(hash);
            cmd.Parameters.AddWithValue(userId);
            cmd.ExecuteNonQuery();

            using var emailCmd = new NpgsqlCommand("SELECT email FROM users WHERE id = $1", owner);
            emailCmd.Parameters.AddWithValue(userId);
            var email = (string)(await emailCmd.ExecuteScalarAsync())!;

            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                BaseAddress = new Uri("https://localhost"),
            });
            var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, "test-password"));
            Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
            return client;
        }
    }

    private async Task<string> IssueDeviceTokenAsync(Guid orgId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return issued.PlaintextToken;
    }

    private async Task<long> CountAuditRowsAsync(Guid entityId, string action)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", connection);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task CreatePriceList_AsManageCatalogAdmin_Succeeds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, _) = await SignedInClientAsync(Permission.ManageCatalog);

        var response = await client.PostAsJsonAsync("/pricing/price-lists", new { name = "Default", isDefault = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreatePriceList_SellerLackingManageCatalog_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, _) = await SignedInClientAsync(Permission.ManageUsers);

        var response = await client.PostAsJsonAsync("/pricing/price-lists", new { name = "Default", isDefault = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreatePriceList_CookieLessCaller_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        var response = await client.PostAsJsonAsync("/pricing/price-lists", new { name = "Default", isDefault = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreatePriceList_DeviceBearerCaller_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        using (var scope = _factory.Services.CreateScope())
        {
            var branchCmdConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            await branchCmdConnection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branchCmdConnection);
            cmd.Parameters.AddWithValue(branchId);
            cmd.Parameters.AddWithValue(orgId);
            await cmd.ExecuteNonQueryAsync();
            await branchCmdConnection.DisposeAsync();
        }
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/pricing/price-lists")
        {
            Content = JsonContent.Create(new { name = "Default", isDefault = true }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPriceList_CrossOrganizationTarget_Returns404_IdenticalToNonexistent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (ownerClient, ownerOrgId) = await SignedInClientAsync(Permission.ManageCatalog);
        var createResponse = await ownerClient.PostAsJsonAsync("/pricing/price-lists", new { name = "Org A List", isDefault = true });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var priceListId = created.GetProperty("id").GetGuid();

        var (otherOrgClient, _) = await SignedInClientAsync(Permission.ManageCatalog);

        var crossOrgResponse = await otherOrgClient.GetAsync($"/pricing/price-lists/{priceListId}");
        var nonexistentResponse = await otherOrgClient.GetAsync($"/pricing/price-lists/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, crossOrgResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistentResponse.StatusCode);
    }

    [Fact]
    public async Task AppendEntry_ThenGetHistory_ReturnsPublishedEntry_AndWritesAuditRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, orgId) = await SignedInClientAsync(Permission.ManageCatalog);
        var presentationId = await SeedPresentationAsync(orgId);

        var createResponse = await client.PostAsJsonAsync("/pricing/price-lists", new { name = "Default", isDefault = true });
        var priceList = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var priceListId = priceList.GetProperty("id").GetGuid();

        var appendResponse = await client.PostAsJsonAsync(
            $"/pricing/price-lists/{priceListId}/entries",
            new { presentationId, unitPrice = 100.00m, effectiveFrom = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, appendResponse.StatusCode);
        var entry = await appendResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = entry.GetProperty("id").GetGuid();

        var historyResponse = await client.GetAsync($"/pricing/price-lists/{priceListId}/presentations/{presentationId}/history");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(history!, e => e.GetProperty("id").GetGuid() == entryId);

        var auditRowCount = await CountAuditRowsAsync(entryId, "price-list-entry.published");
        Assert.Equal(1, auditRowCount);
    }
}
