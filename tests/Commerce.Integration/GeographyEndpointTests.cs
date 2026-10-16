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
/// core-geography: `/geo/provinces`, `/geo/cities` (search, one city) readable by
/// any signed-in user, and the system-administrator-only city writes.
/// </summary>
[Collection("Postgres")]
public sealed class GeographyEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public GeographyEndpointTests(WebApplicationFactory<Program> factory)
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
        // Cities a previous run added by hand (no INDEC id) must not leak into this one.
        using var cleanup = new NpgsqlCommand("DELETE FROM cities WHERE indec_id IS NULL AND NOT EXISTS (SELECT 1 FROM customers WHERE city_id = cities.id)", owner);
        cleanup.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email, bool systemAdmin = false)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        if (systemAdmin)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>()
                .PromoteToSystemAdminAsync(new CloudTenantScope(organizationId), body!.UserId, CancellationToken.None);
        }
        return (organizationId, body!.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, Guid? branchId = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        if (branchId is not null) client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branchId.ToString());
        return client;
    }

    private async Task<string> CreateCashierAsync(Guid organizationId, Guid branchId)
    {
        var userId = Guid.NewGuid();
        var email = $"cashier-{userId:N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
        var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), Password);
        var outcome = await store.CreateStaffUserAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
            new UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId, "user", userId, "user.created", null, null),
            CancellationToken.None);
        Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        return email;
    }

    private static long CountAudit(Guid entityId, string action)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", owner);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)cmd.ExecuteScalar()!;
    }

    private static string[] Names(JsonElement list) => list.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task Provinces_AreListedForAnySignedInUser_ByName()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, _) = await BootstrapAsync("geo-prov@example.com");
        var cashier = await SignInAsync(await CreateCashierAsync(orgId, branchId), branchId);

        var response = await cashier.GetAsync("/geo/provinces");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(24, list.GetArrayLength());
        var buenosAires = list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "06");
        Assert.Equal("AR-B", buenosAires.GetProperty("isoCode").GetString());
        Assert.Equal("Buenos Aires", buenosAires.GetProperty("name").GetString());
        Assert.Equal("AR", buenosAires.GetProperty("countryCode").GetString());
        Assert.Equal("Argentina", buenosAires.GetProperty("countryName").GetString());
        Assert.Equal(Names(list).OrderBy(n => n, StringComparer.Create(new System.Globalization.CultureInfo("es-AR"), true)).ToArray(), Names(list));
    }

    [Fact]
    public async Task Geography_RequiresSignIn()
    {
        if (!_postgresAvailable) return;
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { "/geo/provinces", "/geo/cities", $"/geo/cities/{Guid.NewGuid()}" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.True(response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, url);
        }
    }

    [Fact]
    public async Task Cities_SearchIsAccentAndCaseInsensitive_PrefixFirst_AndHonoursProvinceAndLimit()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("geo-search@example.com");
        var admin = await SignInAsync("geo-search@example.com");

        var found = await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=CAPITAN%20sarm&provinceId=06");
        var sarmiento = found.EnumerateArray().Single();
        Assert.Equal("Capitán Sarmiento", sarmiento.GetProperty("name").GetString());
        Assert.Equal("06140010", sarmiento.GetProperty("indecId").GetString());
        Assert.Equal("06", sarmiento.GetProperty("provinceId").GetString());
        Assert.Equal("Buenos Aires", sarmiento.GetProperty("provinceName").GetString());
        Assert.Equal("AR", sarmiento.GetProperty("countryCode").GetString());
        Assert.Equal("Capitán Sarmiento", sarmiento.GetProperty("departmentName").GetString());
        Assert.True(sarmiento.GetProperty("isActive").GetBoolean());

        // Prefix matches come before "contains" matches.
        Assert.Equal(["Pedro Luro", "San Pedro"], Names(await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=PEDRO&provinceId=06")));

        // The same search with the accent typed and without the province filter.
        Assert.Contains("Capitán Sarmiento", Names(await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=capitán")));

        // Default page is 20 rows, `limit` is honoured and clamped to 200.
        Assert.Equal(20, (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=san")).GetArrayLength());
        Assert.Equal(5, (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=san&limit=5")).GetArrayLength());
        Assert.Equal(200, (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=san&limit=100000")).GetArrayLength());

        // LIKE wildcards are data, not syntax.
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=%25")).GetArrayLength());
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=sa_to")).GetArrayLength());

        // Province filter alone lists that province only.
        var caba = await admin.GetFromJsonAsync<JsonElement>("/geo/cities?provinceId=02&limit=200");
        Assert.All(caba.EnumerateArray(), c => Assert.Equal("02", c.GetProperty("provinceId").GetString()));
        Assert.Contains("Ciudad de Buenos Aires", Names(caba));
    }

    [Fact]
    public async Task City_CanBeFetchedById_And404WhenUnknown()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("geo-get@example.com");
        var admin = await SignInAsync("geo-get@example.com");
        var id = (await admin.GetFromJsonAsync<JsonElement>("/geo/cities?search=rio%20tala&provinceId=06")).EnumerateArray().Single().GetProperty("id").GetGuid();

        var city = await admin.GetFromJsonAsync<JsonElement>($"/geo/cities/{id}");
        Assert.Equal("Río Tala", city.GetProperty("name").GetString());
        Assert.Equal("06770040", city.GetProperty("indecId").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/geo/cities/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheSystemAdministrator_CanCreateEditAndDeactivateCities()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("geo-owner@example.com");                       // business admin, not a sysadmin
        await BootstrapAsync("geo-sysadmin@example.com", systemAdmin: true);
        var businessAdmin = await SignInAsync("geo-owner@example.com");
        var sysadmin = await SignInAsync("geo-sysadmin@example.com");
        var name = "Pueblo Nuevo " + Guid.NewGuid().ToString("N")[..6];

        Assert.Equal(HttpStatusCode.Forbidden, (await businessAdmin.PostAsJsonAsync("/geo/cities", new { name, provinceId = "06" })).StatusCode);

        var createdResponse = await sysadmin.PostAsJsonAsync("/geo/cities", new { name = "  " + name + "  ", provinceId = "06", departmentName = "Salto" });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(name, created.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("indecId").ValueKind);
        Assert.Equal("Buenos Aires", created.GetProperty("provinceName").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, CountAudit(id, "city.created"));

        // Everyone can read it; duplicates and bad input are refused.
        Assert.Equal(id, (await businessAdmin.GetFromJsonAsync<JsonElement>($"/geo/cities/{id}")).GetProperty("id").GetGuid());
        var duplicate = await sysadmin.PostAsJsonAsync("/geo/cities", new { name = name.ToUpperInvariant(), provinceId = "06", departmentName = "salto" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("city-name-in-use", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await sysadmin.PostAsJsonAsync("/geo/cities", new { name = "  ", provinceId = "06" })).StatusCode);
        var badProvince = await sysadmin.PostAsJsonAsync("/geo/cities", new { name = "Otro " + name, provinceId = "99" });
        Assert.Equal(HttpStatusCode.BadRequest, badProvince.StatusCode);
        Assert.Contains("provinceId", await badProvince.Content.ReadAsStringAsync());

        // Edit: rename keeps what is omitted; the business admin may not.
        Assert.Equal(HttpStatusCode.Forbidden, (await businessAdmin.PutAsJsonAsync($"/geo/cities/{id}", new { name = "Robada" })).StatusCode);
        var renamed = await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name = name + " Centro" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedBody = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name + " Centro", renamedBody.GetProperty("name").GetString());
        Assert.Equal("Salto", renamedBody.GetProperty("departmentName").GetString());
        Assert.True(renamedBody.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, CountAudit(id, "city.updated"));

        // Deactivate: hidden from the default search, visible with includeInactive.
        var deactivated = await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name = name + " Centro", isActive = false });
        Assert.False((await deactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());
        var search = $"/geo/cities?search={Uri.EscapeDataString(name)}";
        Assert.Equal(0, (await businessAdmin.GetFromJsonAsync<JsonElement>(search)).GetArrayLength());
        Assert.Equal(1, (await businessAdmin.GetFromJsonAsync<JsonElement>(search + "&includeInactive=true")).GetArrayLength());

        // A Georef city can be renamed but keeps its INDEC id; unknown ids are 404.
        // (Pueblo Doyle, not a city the Vaca Verde seed restores audit dates on.)
        var doyle = (await sysadmin.GetFromJsonAsync<JsonElement>("/geo/cities?search=pueblo%20doyle&provinceId=06")).EnumerateArray().Single();
        var georefRename = await sysadmin.PutAsJsonAsync($"/geo/cities/{doyle.GetProperty("id").GetGuid()}", new { name = "Pueblo Doyle" });
        Assert.Equal("06770030", (await georefRename.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("indecId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.PutAsJsonAsync($"/geo/cities/{Guid.NewGuid()}", new { name = "Nada" })).StatusCode);
    }

    [Fact]
    public async Task Customer_ReferencesAGlobalCity_AndReturnsItsNameAndProvince()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("geo-cust-a@example.com");
        await BootstrapAsync("geo-cust-b@example.com");
        var adminA = await SignInAsync("geo-cust-a@example.com");
        var adminB = await SignInAsync("geo-cust-b@example.com");
        var cityId = (await adminA.GetFromJsonAsync<JsonElement>("/geo/cities?search=rio%20tala&provinceId=06")).EnumerateArray().Single().GetProperty("id").GetGuid();

        // The same global city serves every organization.
        foreach (var (admin, label) in new[] { (adminA, "A"), (adminB, "B") })
        {
            var create = await admin.PostAsJsonAsync("/customers", new
            {
                customerKind = "Retail", displayName = "Cliente " + label, taxIdType = "None", taxCondition = "ConsumidorFinal", cityId,
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var customerId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerId").GetGuid();
            var fetched = await admin.GetFromJsonAsync<JsonElement>($"/customers/{customerId}");
            Assert.Equal(cityId, fetched.GetProperty("cityId").GetGuid());
            Assert.Equal("Río Tala", fetched.GetProperty("cityName").GetString());
            Assert.Equal("06", fetched.GetProperty("provinceId").GetString());
            Assert.Equal("Buenos Aires", fetched.GetProperty("provinceName").GetString());
        }

        var unknown = await adminA.PostAsJsonAsync("/customers", new
        {
            customerKind = "Retail", displayName = "Sin ciudad", taxIdType = "None", taxCondition = "ConsumidorFinal", cityId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("cityId", await unknown.Content.ReadAsStringAsync());

        // The retired organization endpoints are gone (a GET falls through to the web app, a POST has no route).
        Assert.False((await adminA.PostAsJsonAsync("/customers/cities", new { name = "Moreno" })).IsSuccessStatusCode);
    }
}
