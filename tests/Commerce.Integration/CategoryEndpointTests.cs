using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// catalog-categories spec: organization-scoped category CRUD, admin-only
/// writes with staff read access, cross-organization isolation, the product
/// category reference, and delete-while-in-use refusal.
/// </summary>
[Collection("Postgres")]
public sealed class CategoryEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string OrganizationSelectorHeader = "X-Organization-Id";
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CategoryEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndReset();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE audit_log, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, Guid? branchId = null, Guid? actingOrganizationId = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        if (branchId is not null) client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branchId.ToString());
        if (actingOrganizationId is not null) client.DefaultRequestHeaders.Add(OrganizationSelectorHeader, actingOrganizationId.ToString());
        return client;
    }

    /// <summary>A second user in the SAME organization holding only ViewSales (a cashier).</summary>
    private async Task<string> CreateCashierAsync(Guid organizationId, Guid branchId)
    {
        var userId = Guid.NewGuid();
        var email = $"cashier-{userId:N}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
            var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), Password);
            var outcome = await store.CreateStaffUserAsync(
                new CloudTenantScope(organizationId),
                new NewUserAccount(userId, email, hash, [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
                new UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId, "user", userId, "user.created", null, null),
                CancellationToken.None);
            Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        }
        return email;
    }

    private static void PromoteToSystemAdmin(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE id = $1", owner);
        flag.Parameters.AddWithValue(userId);
        flag.ExecuteNonQuery();
    }

    /// <summary>
    /// The staff cookie scheme answers a refused caller with a redirect to its
    /// login / access-denied path (`Results.Forbid()`), other routes with 401/403.
    /// Any of them means "refused"; what must never happen is a 2xx.
    /// </summary>
    private static void AssertRefused(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected a refusal but got {(int)response.StatusCode}.");

    private static async Task<CategoryRecord> CreateCategoryAsync(HttpClient client, string name, string iconKey = "meat")
    {
        var response = await client.PostAsJsonAsync("/catalog/categories", new { name, iconKey });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CategoryRecord>())!;
    }

    private static async Task<JsonElement> CreateProductAsync(HttpClient client, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync("/catalog/products", body);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Admin_CreatesListsRenamesAndDeletesACategory()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("cat-admin@example.com");
        var admin = await SignInAsync("cat-admin@example.com");

        var created = await CreateCategoryAsync(admin, "  Carnes  ", "meat");
        Assert.Equal("Carnes", created.Name);
        Assert.Equal("meat", created.IconKey);

        var renamed = await admin.PutAsJsonAsync($"/catalog/categories/{created.Id}", new { name = "Carnes rojas", iconKey = "charcoal" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var updated = (await renamed.Content.ReadFromJsonAsync<CategoryRecord>())!;
        Assert.Equal("Carnes rojas", updated.Name);
        Assert.Equal("charcoal", updated.IconKey);

        var list = await admin.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories");
        Assert.Single(list!);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/catalog/categories/{created.Id}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/catalog/categories/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Create_WithDuplicateNameOrUnknownIcon_IsRefused()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("cat-dup@example.com");
        var admin = await SignInAsync("cat-dup@example.com");
        await CreateCategoryAsync(admin, "Carnes");

        var duplicate = await admin.PostAsJsonAsync("/catalog/categories", new { name = " carnes ", iconKey = "meat" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var badIcon = await admin.PostAsJsonAsync("/catalog/categories", new { name = "Otra", iconKey = "spaceship" });
        Assert.Equal(HttpStatusCode.BadRequest, badIcon.StatusCode);

        var blank = await admin.PostAsJsonAsync("/catalog/categories", new { name = "   ", iconKey = "meat" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task Staff_CanListCategories_ButCannotWrite()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, _) = await BootstrapAsync("cat-staff-admin@example.com");
        var admin = await SignInAsync("cat-staff-admin@example.com");
        var category = await CreateCategoryAsync(admin, "Vinos", "wine");
        var cashierEmail = await CreateCashierAsync(organizationId, branchId);
        var cashier = await SignInAsync(cashierEmail);

        var list = await cashier.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories");
        Assert.Single(list!);

        AssertRefused(await cashier.PostAsJsonAsync("/catalog/categories", new { name = "Nueva", iconKey = "meat" }));
        AssertRefused(await cashier.PutAsJsonAsync($"/catalog/categories/{category.Id}", new { name = "X", iconKey = "meat" }));
        AssertRefused(await cashier.DeleteAsync($"/catalog/categories/{category.Id}"));
    }

    [Fact]
    public async Task Unauthenticated_IsRefused()
    {
        if (!_postgresAvailable) return;
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var response = await anonymous.GetAsync("/catalog/categories");
        AssertRefused(response);
    }

    [Fact]
    public async Task CrossOrganization_CategoryIsInvisible_AndCannotBeUsedByAProduct()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("cat-org-a@example.com");
        var (_, branchB, _) = await BootstrapAsync("cat-org-b@example.com");
        var adminA = await SignInAsync("cat-org-a@example.com");
        var adminB = await SignInAsync("cat-org-b@example.com", branchB);
        var categoryOfA = await CreateCategoryAsync(adminA, "Carnes");

        Assert.Empty((await adminB.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories"))!);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminB.PutAsJsonAsync($"/catalog/categories/{categoryOfA.Id}", new { name = "Robada", iconKey = "meat" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/catalog/categories/{categoryOfA.Id}")).StatusCode);

        await CreateProductAsync(adminB, new { name = "Bife", categoryId = categoryOfA.Id, defaultUnitId = Guid.NewGuid() }, HttpStatusCode.BadRequest);

        // Organization A still owns its untouched category.
        var stillThere = await adminA.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories");
        Assert.Equal("Carnes", Assert.Single(stillThere!).Name);
    }

    [Fact]
    public async Task SameCategoryName_IsAllowedInTwoOrganizations()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("cat-same-a@example.com");
        await BootstrapAsync("cat-same-b@example.com");
        var adminA = await SignInAsync("cat-same-a@example.com");
        var adminB = await SignInAsync("cat-same-b@example.com");

        await CreateCategoryAsync(adminA, "Carnes");
        await CreateCategoryAsync(adminB, "Carnes");
    }

    [Fact]
    public async Task Sysadmin_ActingOnAnOrganization_ManagesThatOrganizationsCategories()
    {
        if (!_postgresAvailable) return;
        var (_, _, sysadminId) = await BootstrapAsync("cat-sysadmin@example.com");
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, _, _) = await BootstrapAsync("cat-sysadmin-target@example.com");
        var sysadmin = await SignInAsync("cat-sysadmin@example.com", actingOrganizationId: targetOrgId);
        var targetAdmin = await SignInAsync("cat-sysadmin-target@example.com");

        await CreateCategoryAsync(sysadmin, "Almacén", "grocery");

        var seenByTarget = await targetAdmin.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories");
        Assert.Equal("Almacén", Assert.Single(seenByTarget!).Name);
    }

    [Fact]
    public async Task ProductWithoutCategory_UsesTheDefault_CreatedOnDemandOnce()
    {
        if (!_postgresAvailable) return;
        var (_, branchId, _) = await BootstrapAsync("cat-default@example.com");
        var admin = await SignInAsync("cat-default@example.com", branchId);

        var first = await CreateProductAsync(admin, new { name = "Harina", defaultUnitId = Guid.NewGuid() });
        var second = await CreateProductAsync(admin, new { name = "Azúcar", categoryId = Guid.Empty, defaultUnitId = Guid.NewGuid() });

        var categories = await admin.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories");
        var fallback = Assert.Single(categories!);
        Assert.Equal("Sin categoría", fallback.Name);
        Assert.Equal("generic", fallback.IconKey);
        Assert.Equal(fallback.Id, first.GetProperty("categoryId").GetGuid());
        Assert.Equal(fallback.Id, second.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Delete_WhileAProductUsesTheCategory_IsRefusedWithConflict()
    {
        if (!_postgresAvailable) return;
        var (_, branchId, _) = await BootstrapAsync("cat-inuse@example.com");
        var admin = await SignInAsync("cat-inuse@example.com", branchId);
        var category = await CreateCategoryAsync(admin, "Bebidas", "drinks");
        await CreateProductAsync(admin, new { name = "Agua", categoryId = category.Id, defaultUnitId = Guid.NewGuid() });

        var response = await admin.DeleteAsync($"/catalog/categories/{category.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("category-in-use", await response.Content.ReadAsStringAsync());
        Assert.Single((await admin.GetFromJsonAsync<List<CategoryRecord>>("/catalog/categories"))!);
    }

    [Fact]
    public async Task ChangeProductCategory_AcceptsOwnCategory_AndRefusesUnknownOrForeign()
    {
        if (!_postgresAvailable) return;
        var (_, branchA, _) = await BootstrapAsync("cat-change-a@example.com");
        var (_, branchB, _) = await BootstrapAsync("cat-change-b@example.com");
        var adminA = await SignInAsync("cat-change-a@example.com", branchA);
        var adminB = await SignInAsync("cat-change-b@example.com", branchB);
        var product = await CreateProductAsync(adminA, new { name = "Bife", defaultUnitId = Guid.NewGuid() });
        var productId = product.GetProperty("id").GetGuid();
        var own = await CreateCategoryAsync(adminA, "Carnes");
        var foreign = await CreateCategoryAsync(adminB, "Ajena");

        var ok = await adminA.PutAsJsonAsync($"/catalog/products/{productId}/category", new { categoryId = own.Id });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(own.Id, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("categoryId").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminA.PutAsJsonAsync($"/catalog/products/{productId}/category", new { categoryId = foreign.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await adminA.PutAsJsonAsync($"/catalog/products/{productId}/category", new { categoryId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await adminA.PutAsJsonAsync($"/catalog/products/{Guid.NewGuid()}/category", new { categoryId = own.Id })).StatusCode);
    }
}
