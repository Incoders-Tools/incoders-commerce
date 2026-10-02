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
/// customer-master-data: organization-scoped Cities and Business Types
/// (read for any staff user, write needs ManageUsers, soft-disable only), and
/// the customer endpoints carrying city, business type, contact name, Dni and
/// the list filters.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerMasterDataEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerMasterDataEndpointTests(WebApplicationFactory<Program> factory)
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

    private static void AssertRefused(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected a refusal but got {(int)response.StatusCode}.");

    private static long CountAudit(Guid entityId, string action)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", owner);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)cmd.ExecuteScalar()!;
    }

    private static async Task<JsonElement> PostOkAsync(HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object CustomerBody(string displayName, string taxIdType = "None", string? taxId = null,
        Guid? cityId = null, Guid? businessTypeId = null, string? contactName = null) => new
    {
        customerKind = "Retail", displayName, taxIdType, taxId, taxCondition = "ConsumidorFinal",
        cityId, businessTypeId, contactName,
    };

    public static TheoryData<string, string> Catalogs => new()
    {
        { "/customers/cities", "city" },
        { "/customers/business-types", "business_type" },
    };

    // ------------------------------------------------------------------
    // Cities / business types
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Admin_CreatesEntry_WithDerivedKey_ListsRenamesAndDisablesIt_WithAudit(string route, string auditPrefix)
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync($"md-admin-{auditPrefix}@example.com");
        var admin = await SignInAsync($"md-admin-{auditPrefix}@example.com");

        var created = await PostOkAsync(admin, route, new { name = "  San Miguel  " });
        Assert.Equal("San Miguel", created.GetProperty("name").GetString());
        Assert.Equal("san_miguel", created.GetProperty("key").GetString());
        Assert.Equal(0, created.GetProperty("sortOrder").GetInt32());
        Assert.True(created.GetProperty("isActive").GetBoolean());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(1, CountAudit(id, auditPrefix + ".created"));

        var explicitKey = await PostOkAsync(admin, route, new { name = "Moreno", key = "mrn", sortOrder = 5 });
        Assert.Equal("mrn", explicitKey.GetProperty("key").GetString());
        Assert.Equal(5, explicitKey.GetProperty("sortOrder").GetInt32());

        var listed = await admin.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(["San Miguel", "Moreno"], listed.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());

        var renamed = await admin.PutAsJsonAsync($"{route}/{id}", new { name = "San Miguel Centro", sortOrder = 1, isActive = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var renamedBody = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("San Miguel Centro", renamedBody.GetProperty("name").GetString());
        Assert.Equal("san_miguel", renamedBody.GetProperty("key").GetString()); // key kept when not supplied
        Assert.False(renamedBody.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, CountAudit(id, auditPrefix + ".updated"));

        var active = await admin.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(["Moreno"], active.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());
        var all = await admin.GetFromJsonAsync<JsonElement>(route + "?includeInactive=true");
        Assert.Equal(2, all.GetArrayLength());

        var delete = await admin.DeleteAsync($"{route}/{id}");
        Assert.False(delete.IsSuccessStatusCode);
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Update_KeepsTheStoredIsActive_WhenTheFieldIsOmitted(string route, string auditPrefix)
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync($"md-keep-{auditPrefix}@example.com");
        var admin = await SignInAsync($"md-keep-{auditPrefix}@example.com");
        var id = (await PostOkAsync(admin, route, new { name = "Moreno", isActive = false })).GetProperty("id").GetGuid();

        var renamed = await admin.PutAsJsonAsync($"{route}/{id}", new { name = "Moreno Centro" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.False((await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());

        var reactivated = await admin.PutAsJsonAsync($"{route}/{id}", new { name = "Moreno Centro", isActive = true });
        Assert.True((await reactivated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());
        var stillActive = await admin.PutAsJsonAsync($"{route}/{id}", new { name = "Moreno Norte" });
        Assert.True((await stillActive.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Create_RejectsBlankName_AndDuplicateNameOrKey(string route, string auditPrefix)
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync($"md-dup-{auditPrefix}@example.com");
        var admin = await SignInAsync($"md-dup-{auditPrefix}@example.com");

        await PostOkAsync(admin, route, new { name = "Moreno" });
        await PostOkAsync(admin, route, new { name = "moreno" }, HttpStatusCode.Conflict);
        await PostOkAsync(admin, route, new { name = "Otro", key = "moreno" }, HttpStatusCode.Conflict);
        await PostOkAsync(admin, route, new { name = "   " }, HttpStatusCode.BadRequest);
        await PostOkAsync(admin, route, new { name = "!!!" }, HttpStatusCode.BadRequest); // no derivable key
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task StaffUserWithoutManageUsers_CanReadButNotWrite(string route, string auditPrefix)
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, _) = await BootstrapAsync($"md-owner-{auditPrefix}@example.com");
        var admin = await SignInAsync($"md-owner-{auditPrefix}@example.com");
        var created = await PostOkAsync(admin, route, new { name = "Moreno" });

        var cashier = await SignInAsync(await CreateCashierAsync(orgId, branchId), branchId);
        var list = await cashier.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(1, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

        AssertRefused(await cashier.PostAsJsonAsync(route, new { name = "Nuevo" }));
        AssertRefused(await cashier.PutAsJsonAsync($"{route}/{created.GetProperty("id").GetGuid()}", new { name = "X", isActive = true }));
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public async Task Entries_AreInvisibleToOtherOrganizations(string route, string auditPrefix)
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync($"md-a-{auditPrefix}@example.com");
        await BootstrapAsync($"md-b-{auditPrefix}@example.com");
        var adminA = await SignInAsync($"md-a-{auditPrefix}@example.com");
        var adminB = await SignInAsync($"md-b-{auditPrefix}@example.com");

        var created = await PostOkAsync(adminA, route, new { name = "Moreno" });
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(0, (await adminB.GetFromJsonAsync<JsonElement>(route)).GetArrayLength());
        var foreignPut = await adminB.PutAsJsonAsync($"{route}/{id}", new { name = "Robada", isActive = true });
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
        await PostOkAsync(adminB, route, new { name = "Moreno" }); // same name is fine in another org
    }

    // ------------------------------------------------------------------
    // Customers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Customer_CarriesCityBusinessTypeContactAndDni_AndReturnsTheNames()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-cust@example.com");
        var admin = await SignInAsync("md-cust@example.com");
        var city = await PostOkAsync(admin, "/customers/cities", new { name = "Moreno" });
        var type = await PostOkAsync(admin, "/customers/business-types", new { name = "Bar" });
        var cityId = city.GetProperty("id").GetGuid();
        var typeId = type.GetProperty("id").GetGuid();

        var created = await PostOkAsync(admin, "/customers",
            CustomerBody("Bar Pepe", "Dni", "12.345.678", cityId, typeId, "Pepe"));
        var customerId = created.GetProperty("customerId").GetGuid();

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/customers/{customerId}");
        Assert.Equal(cityId, fetched.GetProperty("cityId").GetGuid());
        Assert.Equal("Moreno", fetched.GetProperty("cityName").GetString());
        Assert.Equal(typeId, fetched.GetProperty("businessTypeId").GetGuid());
        Assert.Equal("Bar", fetched.GetProperty("businessTypeName").GetString());
        Assert.Equal("Pepe", fetched.GetProperty("contactName").GetString());
        Assert.Equal("Dni", fetched.GetProperty("taxIdType").GetString());
        Assert.Equal("12345678", fetched.GetProperty("taxId").GetString()); // stored digits only

        var cuit = await PostOkAsync(admin, "/customers", CustomerBody("Acme", "Cuit", "30-12345678-9"));
        var cuitFetched = await admin.GetFromJsonAsync<JsonElement>($"/customers/{cuit.GetProperty("customerId").GetGuid()}");
        Assert.Equal("30123456789", cuitFetched.GetProperty("taxId").GetString());
    }

    [Theory]
    [InlineData("Dni", "123")]
    [InlineData("Dni", "ABCDEFGH")]
    [InlineData("Cuit", "12345")]
    [InlineData("Cuil", null)]
    public async Task Customer_WithMalformedTaxId_Returns400(string taxIdType, string? taxId)
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync($"md-tax-{taxIdType}-{taxId}@example.com");
        var admin = await SignInAsync($"md-tax-{taxIdType}-{taxId}@example.com");

        var response = await admin.PostAsJsonAsync("/customers", CustomerBody("Mal", taxIdType, taxId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("taxId", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Customer_WithACityOrBusinessTypeOfAnotherOrganization_Returns400()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-x-a@example.com");
        await BootstrapAsync("md-x-b@example.com");
        var adminA = await SignInAsync("md-x-a@example.com");
        var adminB = await SignInAsync("md-x-b@example.com");
        var cityOfB = (await PostOkAsync(adminB, "/customers/cities", new { name = "Moreno" })).GetProperty("id").GetGuid();
        var typeOfB = (await PostOkAsync(adminB, "/customers/business-types", new { name = "Bar" })).GetProperty("id").GetGuid();

        var foreignCity = await adminA.PostAsJsonAsync("/customers", CustomerBody("Cliente", cityId: cityOfB));
        Assert.Equal(HttpStatusCode.BadRequest, foreignCity.StatusCode);
        Assert.Contains("cityId", await foreignCity.Content.ReadAsStringAsync());

        var foreignType = await adminA.PostAsJsonAsync("/customers", CustomerBody("Cliente", businessTypeId: typeOfB));
        Assert.Equal(HttpStatusCode.BadRequest, foreignType.StatusCode);
        Assert.Contains("businessTypeId", await foreignType.Content.ReadAsStringAsync());

        var unknown = await adminA.PostAsJsonAsync("/customers", CustomerBody("Cliente", cityId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Customers_ListFiltersBySearchCityAndBusinessType_OrderedByName()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-list@example.com");
        var admin = await SignInAsync("md-list@example.com");
        var moreno = (await PostOkAsync(admin, "/customers/cities", new { name = "Moreno" })).GetProperty("id").GetGuid();
        var merlo = (await PostOkAsync(admin, "/customers/cities", new { name = "Merlo" })).GetProperty("id").GetGuid();
        var bar = (await PostOkAsync(admin, "/customers/business-types", new { name = "Bar" })).GetProperty("id").GetGuid();
        var resto = (await PostOkAsync(admin, "/customers/business-types", new { name = "Restaurante" })).GetProperty("id").GetGuid();

        await PostOkAsync(admin, "/customers", CustomerBody("Zeta Resto", cityId: moreno, businessTypeId: resto, contactName: "Marta"));
        await PostOkAsync(admin, "/customers", CustomerBody("José Bar", cityId: merlo, businessTypeId: bar, contactName: "Ñoño"));
        await PostOkAsync(admin, "/customers", CustomerBody("Alfa Bar", "Dni", "20123456", moreno, bar, "Luis"));

        static string[] Names(JsonElement list) => list.EnumerateArray().Select(e => e.GetProperty("displayName").GetString()!).ToArray();
        async Task<string[]> Query(string qs) => Names(await admin.GetFromJsonAsync<JsonElement>("/customers" + qs));

        Assert.Equal(["Alfa Bar", "José Bar", "Zeta Resto"], await Query(""));
        Assert.Equal(["José Bar"], await Query("?search=jose"));        // accent- and case-insensitive
        Assert.Equal(["José Bar"], await Query("?search=NONO"));        // contact name, ñ folded
        Assert.Equal(["Zeta Resto"], await Query("?search=marta"));
        Assert.Equal(["Alfa Bar"], await Query("?search=20123456"));    // tax id
        Assert.Equal(["Alfa Bar", "José Bar"], await Query("?search=bar"));
        Assert.Equal(["Alfa Bar", "Zeta Resto"], await Query($"?cityId={moreno}"));
        Assert.Equal(["Alfa Bar", "José Bar"], await Query($"?businessTypeId={bar}"));
        Assert.Equal(["Alfa Bar"], await Query($"?cityId={moreno}&businessTypeId={bar}&search=alfa"));
        Assert.Equal(["Alfa Bar", "José Bar", "Zeta Resto"], await Query("?search="));
        Assert.Empty(await Query("?search=100%25")); // LIKE wildcards in the term are escaped, not interpreted
    }

    [Fact]
    public async Task CustomerUpdate_KeepsMasterDataWhenOmitted_AndClearsItExplicitly()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-put@example.com");
        var admin = await SignInAsync("md-put@example.com");
        var city = (await PostOkAsync(admin, "/customers/cities", new { name = "Moreno" })).GetProperty("id").GetGuid();
        var type = (await PostOkAsync(admin, "/customers/business-types", new { name = "Bar" })).GetProperty("id").GetGuid();
        var id = (await PostOkAsync(admin, "/customers", CustomerBody("Bar Pepe", cityId: city, businessTypeId: type, contactName: "Pepe")))
            .GetProperty("customerId").GetGuid();

        // A client that predates the new fields (the POS) must not wipe them.
        var legacy = await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Bar Pepe 2", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        var kept = await legacy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(city, kept.GetProperty("cityId").GetGuid());
        Assert.Equal(type, kept.GetProperty("businessTypeId").GetGuid());
        Assert.Equal("Pepe", kept.GetProperty("contactName").GetString());
        Assert.Equal("Moreno", kept.GetProperty("cityName").GetString());

        var cleared = await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Bar Pepe 2", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true,
            cityId = Guid.Empty, businessTypeId = Guid.Empty, contactName = "",
        });
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, clearedBody.GetProperty("cityId").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedBody.GetProperty("businessTypeId").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedBody.GetProperty("contactName").ValueKind);
    }
    private static object PutBody(string displayName, DateTimeOffset? expected = null) => new
    {
        displayName, taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true,
        expectedUpdatedAtUtc = expected,
    };

    [Fact]
    public async Task CustomerUpdate_WithExpectedUpdatedAtUtc_Returns409WhenTheRowChangedInTheMeantime()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-occ@example.com");
        var admin = await SignInAsync("md-occ@example.com");
        var id = (await PostOkAsync(admin, "/customers", CustomerBody("Bar Pepe"))).GetProperty("customerId").GetGuid();
        var loaded = await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}");
        var token = loaded.GetProperty("updatedAtUtc").GetDateTimeOffset();

        var first = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 2", token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var newToken = firstBody.GetProperty("updatedAtUtc").GetDateTimeOffset();
        Assert.True(newToken > token);

        var stale = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Perdido", token));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("customer-modified", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("Bar Pepe 2", (await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}")).GetProperty("displayName").GetString());

        var fresh = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 3", newToken));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // Legacy clients (the POS) send no token: no check, last write wins.
        var legacy = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 4"));
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);

        var unknown = await admin.PutAsJsonAsync($"/customers/{Guid.NewGuid()}", PutBody("Nadie", token));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task CustomerUpdate_TwoConcurrentWritersWithTheSameToken_OnlyOneWins()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("md-race@example.com");
        var admin = await SignInAsync("md-race@example.com");
        var id = (await PostOkAsync(admin, "/customers", CustomerBody("Bar Pepe"))).GetProperty("customerId").GetGuid();
        var token = (await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}")).GetProperty("updatedAtUtc").GetDateTimeOffset();

        for (var round = 0; round < 5; round++)
        {
            var responses = await Task.WhenAll(
                admin.PutAsJsonAsync($"/customers/{id}", PutBody("Writer A", token)),
                admin.PutAsJsonAsync($"/customers/{id}", PutBody("Writer B", token)));
            var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToArray();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);

            var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
            token = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAtUtc").GetDateTimeOffset();
        }
    }
}
