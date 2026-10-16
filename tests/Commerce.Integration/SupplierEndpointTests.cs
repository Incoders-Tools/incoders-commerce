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
/// suppliers: the supplier registry endpoints (`/suppliers`) and the organization-scoped supplier
/// categories catalog (`/suppliers/categories`), modelled on the customer endpoints.
/// </summary>
[Collection("Postgres")]
public sealed partial class SupplierEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public SupplierEndpointTests(WebApplicationFactory<Program> factory)
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

    private async Task<HttpClient> NewAdminAsync(string email)
    {
        await BootstrapAsync(email);
        return await SignInAsync(email);
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

    private static object SupplierBody(
        string displayName, string? legalName = null, string taxIdType = "None", string? taxId = null,
        Guid? cityId = null, Guid? categoryId = null, int? paymentTermsDays = null, string? bankCbu = null,
        string? bankAlias = null, object[]? contacts = null) => new
    {
        displayName, legalName, taxIdType, taxId, taxCondition = "ResponsableInscripto", cityId, categoryId,
        paymentTermsDays, bankCbu, bankAlias, contacts,
    };

    private static async Task<Guid> CreateSupplierAsync(HttpClient client, object body) =>
        (await PostOkAsync(client, "/suppliers", body)).GetProperty("supplierId").GetGuid();

    private static async Task<Guid> GeorefCityAsync(HttpClient client, string name)
    {
        var list = await client.GetFromJsonAsync<JsonElement>($"/geo/cities?search={Uri.EscapeDataString(name)}&provinceId=06&limit=50");
        return list.EnumerateArray().First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }

    private static string[] Names(JsonElement list) =>
        list.EnumerateArray().Select(e => e.GetProperty("displayName").GetString()!).ToArray();

    // ------------------------------------------------------------------
    // Categories
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CreatesCategory_WithDerivedKey_ListsRenamesAndDisablesIt_WithAudit()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-cat-admin@example.com");
        const string route = "/suppliers/categories";

        var created = await PostOkAsync(admin, route, new { name = "  Carne  " });
        Assert.Equal("Carne", created.GetProperty("name").GetString());
        Assert.Equal("carne", created.GetProperty("key").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(1, CountAudit(id, "supplier_category.created"));

        await PostOkAsync(admin, route, new { name = "Tecnología", sortOrder = 2 });

        var renamed = await admin.PutAsJsonAsync($"{route}/{id}", new { name = "Carnes", isActive = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(1, CountAudit(id, "supplier_category.updated"));

        var active = await admin.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(["Tecnología"], active.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, (await admin.GetFromJsonAsync<JsonElement>(route + "?includeInactive=true")).GetArrayLength());

        Assert.False((await admin.DeleteAsync($"{route}/{id}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Category_DuplicateNameOrKey_Returns409_WithSupplierCategoryCodes()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-cat-dup@example.com");

        await PostOkAsync(admin, "/suppliers/categories", new { name = "Limpieza" });
        var byName = await PostOkAsync(admin, "/suppliers/categories", new { name = "limpieza" }, HttpStatusCode.Conflict);
        Assert.Equal("supplier-category-name-in-use", byName.GetProperty("error").GetString());
        var byKey = await PostOkAsync(admin, "/suppliers/categories", new { name = "Otro", key = "limpieza" }, HttpStatusCode.Conflict);
        Assert.Equal("supplier-category-key-in-use", byKey.GetProperty("error").GetString());
        await PostOkAsync(admin, "/suppliers/categories", new { name = "   " }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Category_StaffWithoutManageUsers_CanReadButNotWrite_AndOtherOrganizationsDoNotSeeIt()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, _) = await BootstrapAsync("sup-cat-owner@example.com");
        var admin = await SignInAsync("sup-cat-owner@example.com");
        var created = await PostOkAsync(admin, "/suppliers/categories", new { name = "Carne" });

        var cashier = await SignInAsync(await CreateCashierAsync(orgId, branchId), branchId);
        Assert.Equal(1, (await cashier.GetFromJsonAsync<JsonElement>("/suppliers/categories")).GetArrayLength());
        AssertRefused(await cashier.PostAsJsonAsync("/suppliers/categories", new { name = "Nuevo" }));
        AssertRefused(await cashier.PutAsJsonAsync($"/suppliers/categories/{created.GetProperty("id").GetGuid()}", new { name = "X" }));

        var other = await NewAdminAsync("sup-cat-other@example.com");
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/suppliers/categories")).GetArrayLength());
        var foreignPut = await other.PutAsJsonAsync($"/suppliers/categories/{created.GetProperty("id").GetGuid()}", new { name = "Robada" });
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
    }

    // ------------------------------------------------------------------
    // Suppliers
    // ------------------------------------------------------------------

    [Fact]
    public async Task Admin_CreatesSupplier_WithCategoryCityTaxIdBankAndTwoContacts_AndReadsItBack()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-create@example.com");
        var category = (await PostOkAsync(admin, "/suppliers/categories", new { name = "Carne" })).GetProperty("id").GetGuid();
        var cityId = await GeorefCityAsync(admin, "Moreno");

        var supplierId = await CreateSupplierAsync(admin, SupplierBody(
            "Frigorífico Sur", "Frigorífico Sur SA", "Cuit", "30-12345678-9", cityId, category, 30,
            "0123 4567 8901 2345 6789 01", "frigo.sur",
            [new { firstName = "Ana", lastName = "Pérez", phone = "11-5555", isPrimary = true }, new { firstName = "Beto", role = "Ventas" }]));

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}");
        Assert.Equal("Frigorífico Sur", fetched.GetProperty("displayName").GetString());
        Assert.Equal("Cuit", fetched.GetProperty("taxIdType").GetString());
        Assert.Equal("30123456789", fetched.GetProperty("taxId").GetString());
        Assert.Equal("ResponsableInscripto", fetched.GetProperty("taxCondition").GetString());
        Assert.Equal(cityId, fetched.GetProperty("cityId").GetGuid());
        Assert.Equal("Moreno", fetched.GetProperty("cityName").GetString());
        Assert.Equal("Buenos Aires", fetched.GetProperty("provinceName").GetString());
        Assert.Equal(category, fetched.GetProperty("categoryId").GetGuid());
        Assert.Equal("Carne", fetched.GetProperty("categoryName").GetString());
        Assert.Equal(30, fetched.GetProperty("paymentTermsDays").GetInt32());
        Assert.Equal("0123456789012345678901", fetched.GetProperty("bankCbu").GetString());
        Assert.Equal("frigo.sur", fetched.GetProperty("bankAlias").GetString());
        Assert.True(fetched.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0m, fetched.GetProperty("balance").GetDecimal());
        Assert.True(fetched.TryGetProperty("updatedAtUtc", out _));
        var contacts = fetched.GetProperty("contacts");
        Assert.Equal(2, contacts.GetArrayLength());
        Assert.Equal("Ana", contacts[0].GetProperty("firstName").GetString());
        Assert.True(contacts[0].GetProperty("isPrimary").GetBoolean());
        Assert.Equal("Beto", contacts[1].GetProperty("firstName").GetString());
        Assert.Equal(1, CountAudit(supplierId, "supplier.created"));
    }

    [Fact]
    public async Task Suppliers_ListSearchesNameLegalNameTaxIdAndContacts_FiltersAndOrdersByName()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-list@example.com");
        var carne = (await PostOkAsync(admin, "/suppliers/categories", new { name = "Carne" })).GetProperty("id").GetGuid();
        var limpieza = (await PostOkAsync(admin, "/suppliers/categories", new { name = "Limpieza" })).GetProperty("id").GetGuid();
        var moreno = await GeorefCityAsync(admin, "Moreno");
        var merlo = await GeorefCityAsync(admin, "Merlo");

        var zeta = await CreateSupplierAsync(admin, SupplierBody("Zeta Limpieza", categoryId: limpieza, cityId: merlo));
        await CreateSupplierAsync(admin, SupplierBody("Alfa Carnes", "Alfa SRL", "Cuit", "30-11111111-1", moreno, carne,
            contacts: [new { firstName = "José", lastName = "Núñez" }]));
        await CreateSupplierAsync(admin, SupplierBody("Beta Servicios", categoryId: carne, cityId: merlo));

        Assert.Equal(["Alfa Carnes", "Beta Servicios", "Zeta Limpieza"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers")));
        Assert.Equal(["Alfa Carnes"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=carnes")));
        Assert.Equal(["Alfa Carnes"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=SRL")));
        Assert.Equal(["Alfa Carnes"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=30-11.111111-1")));
        Assert.Equal(["Alfa Carnes"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=jose")));
        Assert.Equal(["Alfa Carnes"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=NUNEZ")));
        Assert.Empty(Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?search=inexistente")));
        Assert.Equal(["Alfa Carnes", "Beta Servicios"], Names(await admin.GetFromJsonAsync<JsonElement>($"/suppliers?categoryId={carne}")));
        Assert.Equal(["Beta Servicios", "Zeta Limpieza"], Names(await admin.GetFromJsonAsync<JsonElement>($"/suppliers?cityId={merlo}")));
        Assert.Equal(["Beta Servicios"], Names(await admin.GetFromJsonAsync<JsonElement>($"/suppliers?cityId={merlo}&categoryId={carne}")));

        var disable = await admin.PutAsJsonAsync($"/suppliers/{zeta}", SupplierUpdateBody("Zeta Limpieza", isEnabled: false));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(["Alfa Carnes", "Beta Servicios"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?enabled=true")));
        Assert.Equal(["Zeta Limpieza"], Names(await admin.GetFromJsonAsync<JsonElement>("/suppliers?enabled=false")));
        Assert.Equal(3, (await admin.GetFromJsonAsync<JsonElement>("/suppliers")).GetArrayLength());
    }

    private static object SupplierUpdateBody(
        string displayName, bool? isEnabled = null, Guid? cityId = null, Guid? categoryId = null, int? paymentTermsDays = null,
        object[]? contacts = null, DateTimeOffset? expectedUpdatedAtUtc = null, bool omitContacts = false) =>
        omitContacts
            ? new { displayName, taxIdType = "None", taxCondition = "NoAplica", isEnabled, cityId, categoryId, paymentTermsDays, expectedUpdatedAtUtc }
            : new { displayName, taxIdType = "None", taxCondition = "NoAplica", isEnabled, cityId, categoryId, paymentTermsDays, contacts, expectedUpdatedAtUtc };

    [Fact]
    public async Task Update_ReplacesContacts_KeepsOmittedOnes_AndKeepsCityCategoryAndEnabledWhenOmitted()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-update@example.com");
        var category = (await PostOkAsync(admin, "/suppliers/categories", new { name = "Carne" })).GetProperty("id").GetGuid();
        var cityId = await GeorefCityAsync(admin, "Moreno");
        var id = await CreateSupplierAsync(admin, SupplierBody("Proveedor", cityId: cityId, categoryId: category,
            contacts: [new { firstName = "Ana", isPrimary = true }, new { firstName = "Beto" }]));
        var original = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{id}");
        var anaId = original.GetProperty("contacts")[0].GetProperty("id").GetGuid();

        // Contacts omitted: kept. City, category and enabled omitted: kept.
        var kept = await admin.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Proveedor Renombrado", omitContacts: true));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var keptBody = await kept.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Proveedor Renombrado", keptBody.GetProperty("displayName").GetString());
        Assert.Equal(2, keptBody.GetProperty("contacts").GetArrayLength());
        Assert.Equal(cityId, keptBody.GetProperty("cityId").GetGuid());
        Assert.Equal(category, keptBody.GetProperty("categoryId").GetGuid());
        Assert.True(keptBody.GetProperty("isEnabled").GetBoolean());

        // Replace-set: Ana kept by id (renamed), Beto removed, Carla added; the city is cleared with an empty guid.
        var replaced = await admin.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Proveedor Renombrado",
            cityId: Guid.Empty,
            contacts: [new { id = anaId, firstName = "Ana María", isPrimary = true }, new { firstName = "Carla" }]));
        var replacedBody = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        var names = replacedBody.GetProperty("contacts").EnumerateArray().Select(c => c.GetProperty("firstName").GetString()!).ToArray();
        Assert.Equal(["Ana María", "Carla"], names);
        Assert.Equal(anaId, replacedBody.GetProperty("contacts")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, replacedBody.GetProperty("cityId").ValueKind);
        Assert.Equal(2, CountAudit(id, "supplier.updated"));

        // An empty array clears them.
        var cleared = await admin.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Proveedor Renombrado", contacts: []));
        Assert.Equal(0, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contacts").GetArrayLength());
    }

    [Fact]
    public async Task Update_WithAStaleExpectedUpdatedAtUtc_Returns409SupplierModified_AndWritesNothing()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("sup-concurrency@example.com");
        var id = await CreateSupplierAsync(admin, SupplierBody("Proveedor", contacts: [new { firstName = "Ana" }]));
        var first = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{id}");
        var token = first.GetProperty("updatedAtUtc").GetDateTimeOffset();

        var fresh = await admin.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Primera edicion", expectedUpdatedAtUtc: token, contacts: [new { firstName = "Beto" }]));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        var stale = await admin.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Segunda edicion", expectedUpdatedAtUtc: token, contacts: [new { firstName = "Carla" }]));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("supplier-modified", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var after = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{id}");
        Assert.Equal("Primera edicion", after.GetProperty("displayName").GetString());
        Assert.Equal("Beto", after.GetProperty("contacts")[0].GetProperty("firstName").GetString());
    }

    [Theory]
    [InlineData("displayName", "  ")]
    [InlineData("taxId", "123")]
    [InlineData("bankCbu", "123")]
    [InlineData("bankAlias", "x y")]
    [InlineData("paymentTermsDays", "-3")]
    public async Task Create_WithInvalidFields_Returns400_NamingTheField(string field, string value)
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync($"sup-invalid-{field}@example.com");
        var body = field switch
        {
            "displayName" => SupplierBody(value),
            "taxId" => SupplierBody("P", taxIdType: "Cuit", taxId: value),
            "bankCbu" => SupplierBody("P", bankCbu: value),
            "bankAlias" => SupplierBody("P", bankAlias: value),
            _ => SupplierBody("P", paymentTermsDays: int.Parse(value)),
        };

        var response = await admin.PostAsJsonAsync("/suppliers", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_WithBadContacts_UnknownCityOrAnotherOrganizationsCategory_Returns400()
    {
        if (!_postgresAvailable) return;
        var adminA = await NewAdminAsync("sup-ref-a@example.com");
        var adminB = await NewAdminAsync("sup-ref-b@example.com");
        var categoryOfB = (await PostOkAsync(adminB, "/suppliers/categories", new { name = "Carne" })).GetProperty("id").GetGuid();

        var foreignCategory = await adminA.PostAsJsonAsync("/suppliers", SupplierBody("P", categoryId: categoryOfB));
        Assert.Equal(HttpStatusCode.BadRequest, foreignCategory.StatusCode);
        Assert.Contains("categoryId", await foreignCategory.Content.ReadAsStringAsync());

        var unknownCity = await adminA.PostAsJsonAsync("/suppliers", SupplierBody("P", cityId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, unknownCity.StatusCode);
        Assert.Contains("cityId", await unknownCity.Content.ReadAsStringAsync());

        var noFirstName = await adminA.PostAsJsonAsync("/suppliers", SupplierBody("P", contacts: [new { lastName = "Sin nombre" }]));
        Assert.Equal(HttpStatusCode.BadRequest, noFirstName.StatusCode);
        Assert.Contains("contacts", await noFirstName.Content.ReadAsStringAsync());

        var twoPrimaries = await adminA.PostAsJsonAsync("/suppliers",
            SupplierBody("P", contacts: [new { firstName = "A", isPrimary = true }, new { firstName = "B", isPrimary = true }]));
        Assert.Equal(HttpStatusCode.BadRequest, twoPrimaries.StatusCode);
    }

    [Fact]
    public async Task Suppliers_AreInvisibleToOtherOrganizations_AndCannotBeDeleted()
    {
        if (!_postgresAvailable) return;
        var adminA = await NewAdminAsync("sup-iso-a@example.com");
        var adminB = await NewAdminAsync("sup-iso-b@example.com");
        var id = await CreateSupplierAsync(adminA, SupplierBody("Privado"));

        Assert.Equal(0, (await adminB.GetFromJsonAsync<JsonElement>("/suppliers")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/suppliers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("Robado"))).StatusCode);

        Assert.False((await adminA.DeleteAsync($"/suppliers/{id}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminA.GetAsync($"/suppliers/{id}")).StatusCode);
    }

    [Fact]
    public async Task Suppliers_RequireManageUsers()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, _) = await BootstrapAsync("sup-perm@example.com");
        var admin = await SignInAsync("sup-perm@example.com");
        var id = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));

        var cashier = await SignInAsync(await CreateCashierAsync(orgId, branchId), branchId);
        AssertRefused(await cashier.GetAsync("/suppliers"));
        AssertRefused(await cashier.GetAsync($"/suppliers/{id}"));
        AssertRefused(await cashier.PostAsJsonAsync("/suppliers", SupplierBody("Nuevo")));
        AssertRefused(await cashier.PutAsJsonAsync($"/suppliers/{id}", SupplierUpdateBody("X")));
        AssertRefused(await _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/suppliers"));
    }
}
