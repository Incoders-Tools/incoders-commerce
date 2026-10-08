using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T2 + T3b (backend): the organization country in the settings, the provinces of the
/// organization's country, the optional postal code of a city, the customer party type (Person | Company) with the
/// legal name / locality / province no longer written, and the shared email rule on customer, contact, staff user
/// and supplier emails.
/// </summary>
[Collection("Postgres")]
public sealed class AdminConsoleFieldFixesEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    // A second country with one province, only for these tests (Argentina is the only country loaded).
    private const string TestCountry = "ZZ";
    private const string TestProvinceId = "98";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public AdminConsoleFieldFixesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndSeedTestCountry();
    }

    public void Dispose()
    {
        if (_postgresAvailable)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            Exec(owner, "UPDATE organizations SET country_code = 'AR' WHERE country_code = $1", TestCountry);
            Exec(owner, "DELETE FROM cities WHERE province_id = $1 OR name LIKE 'acff %'", TestProvinceId);
            Exec(owner, "DELETE FROM provinces WHERE country_code = $1", TestCountry);
            Exec(owner, "DELETE FROM countries WHERE code = $1", TestCountry);
        }

        _factory.Dispose();
    }

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static void OwnerExec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        Exec(owner, sql, args);
    }

    private static object? OwnerScalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd.ExecuteScalar();
    }

    private static void ApplyMigrationsAndSeedTestCountry()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        Exec(owner, "INSERT INTO countries (code, iso3, name) VALUES ($1, 'ZZZ', 'Zetaland') ON CONFLICT (code) DO NOTHING", TestCountry);
        Exec(owner, "INSERT INTO provinces (id, country_code, iso_code, name) VALUES ($1, $2, 'ZZ-N', 'Zeta Norte') ON CONFLICT (id) DO NOTHING", TestProvinceId, TestCountry);
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email, bool systemAdmin = false)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        if (systemAdmin)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>()
                .PromoteToSystemAdminAsync(new CloudTenantScope(organizationId), body.UserId, CancellationToken.None);
        }

        return (organizationId, body.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"expected a '{field}' error, got {body}");
    }

    private static string[] ProvinceIds(JsonElement list) =>
        list.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();

    // ------------------------------------------------------------------
    // Organization country (T2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task OrganizationSettings_CarryTheCountry_DefaultArgentina_AndTheAdminChangesItAlone()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-country");
        await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        var initial = await admin.GetFromJsonAsync<JsonElement>("/account/organization/settings");
        Assert.Equal("AR", initial.GetProperty("countryCode").GetString());

        var put = await admin.PutAsJsonAsync("/account/organization/settings", new { countryCode = " zz " });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var changed = await admin.GetFromJsonAsync<JsonElement>("/account/organization/settings");
        Assert.Equal(TestCountry, changed.GetProperty("countryCode").GetString());
        Assert.Equal("Comma", changed.GetProperty("quantityDecimalSeparator").GetString());

        // Changing the number format alone keeps the country.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/account/organization/settings", new { quantityDecimalSeparator = "Dot" })).StatusCode);
        Assert.Equal(TestCountry, (await admin.GetFromJsonAsync<JsonElement>("/account/organization/settings")).GetProperty("countryCode").GetString());

        await AssertFieldErrorAsync(await admin.PutAsJsonAsync("/account/organization/settings", new { countryCode = "QQ" }), "countryCode");
        await AssertFieldErrorAsync(await admin.PutAsJsonAsync("/account/organization/settings", new { countryCode = "Argentina" }), "countryCode");
        Assert.Equal(TestCountry, (await admin.GetFromJsonAsync<JsonElement>("/account/organization/settings")).GetProperty("countryCode").GetString());
    }

    [Fact]
    public async Task Provinces_AreThoseOfTheCallersOrganizationCountry_AndASysadminWithoutSelectionSeesAll()
    {
        if (!_postgresAvailable) return;
        var argentinaEmail = Unique("acff-prov-ar");
        var zetaEmail = Unique("acff-prov-zz");
        var sysadminEmail = Unique("acff-prov-sys");
        await BootstrapAsync(argentinaEmail);
        var (zetaOrg, _, _) = await BootstrapAsync(zetaEmail);
        await BootstrapAsync(sysadminEmail, systemAdmin: true);
        OwnerExec("UPDATE organizations SET country_code = $1 WHERE id = $2", TestCountry, zetaOrg);

        var argentina = await (await SignInAsync(argentinaEmail)).GetFromJsonAsync<JsonElement>("/geo/provinces");
        Assert.Equal(24, argentina.GetArrayLength());
        Assert.All(argentina.EnumerateArray(), p => Assert.Equal("AR", p.GetProperty("countryCode").GetString()));

        var zeta = await (await SignInAsync(zetaEmail)).GetFromJsonAsync<JsonElement>("/geo/provinces");
        Assert.Equal([TestProvinceId], ProvinceIds(zeta));

        var sysadmin = await SignInAsync(sysadminEmail);
        var all = await sysadmin.GetFromJsonAsync<JsonElement>("/geo/provinces");
        Assert.Contains(TestProvinceId, ProvinceIds(all));
        Assert.Contains("82", ProvinceIds(all));

        sysadmin.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.OrganizationSelectorHeader, zetaOrg.ToString());
        Assert.Equal([TestProvinceId], ProvinceIds(await sysadmin.GetFromJsonAsync<JsonElement>("/geo/provinces")));
    }

    // ------------------------------------------------------------------
    // City postal code (T2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task City_PostalCode_IsCreatedReturnedKeptWhenOmittedClearedWhenBlank_AndValidated()
    {
        if (!_postgresAvailable) return;
        var sysadminEmail = Unique("acff-city-sys");
        await BootstrapAsync(sysadminEmail, systemAdmin: true);
        var sysadmin = await SignInAsync(sysadminEmail);
        var name = "acff " + Guid.NewGuid().ToString("N")[..8];

        var created = await sysadmin.PostAsJsonAsync("/geo/cities", new { name, provinceId = "82", postalCode = " s2000abc " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var city = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = city.GetProperty("id").GetGuid();
        Assert.Equal("S2000ABC", city.GetProperty("postalCode").GetString());

        var searched = (await sysadmin.GetFromJsonAsync<JsonElement>($"/geo/cities?search={Uri.EscapeDataString(name)}")).EnumerateArray().Single();
        Assert.Equal("S2000ABC", searched.GetProperty("postalCode").GetString());
        Assert.Equal("S2000ABC", (await sysadmin.GetFromJsonAsync<JsonElement>($"/geo/cities/{id}")).GetProperty("postalCode").GetString());

        var kept = await (await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("S2000ABC", kept.GetProperty("postalCode").GetString());

        var changed = await (await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name, postalCode = "2000" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2000", changed.GetProperty("postalCode").GetString());

        var cleared = await (await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name, postalCode = "" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("postalCode").ValueKind);

        await AssertFieldErrorAsync(await sysadmin.PutAsJsonAsync($"/geo/cities/{id}", new { name, postalCode = "20" }), "postalCode");
        await AssertFieldErrorAsync(await sysadmin.PostAsJsonAsync("/geo/cities", new { name = name + " b", provinceId = "82", postalCode = "S2000" }), "postalCode");
    }

    // ------------------------------------------------------------------
    // Customer party type, one name (T3b)
    // ------------------------------------------------------------------

    private static object CustomerBody(
        string displayName, string taxIdType = "None", string? taxId = null, string? partyType = null, string? email = null,
        object[]? contacts = null) => new
    {
        customerKind = "Retail", displayName, taxIdType, taxId, taxCondition = "ConsumidorFinal", partyType, email,
        legalName = "Ignored S.A.", locality = "Ignored locality", province = "Ignored province", contacts,
    };

    private static async Task<JsonElement> CreateCustomerAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/customers", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerId").GetGuid();
        return await client.GetFromJsonAsync<JsonElement>($"/customers/{id}");
    }

    [Fact]
    public async Task Customer_PartyType_IsStoredReturnedAndDefaultsFromTheTaxIdType()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-party");
        await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        Assert.Equal("Company", (await CreateCustomerAsync(admin, CustomerBody("Frigorífico Sur", partyType: "Company"))).GetProperty("partyType").GetString());
        Assert.Equal("Company", (await CreateCustomerAsync(admin, CustomerBody("Sin tipo CUIT", "Cuit", "30-12345678-9"))).GetProperty("partyType").GetString());
        Assert.Equal("Person", (await CreateCustomerAsync(admin, CustomerBody("Ana Pérez"))).GetProperty("partyType").GetString());
        Assert.Equal("Person", (await CreateCustomerAsync(admin, CustomerBody("Juan con CUIT", "Cuit", "20123456789", partyType: "Person"))).GetProperty("partyType").GetString());

        await AssertFieldErrorAsync(await admin.PostAsJsonAsync("/customers", CustomerBody("Malo", partyType: "Empresa")), "partyType");
    }

    [Fact]
    public async Task Customer_AdminApi_NoLongerWritesLegalNameLocalityOrProvince_AndUpdateKeepsOrChangesThePartyType()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-legacy");
        await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        var created = await CreateCustomerAsync(admin, CustomerBody("Carnicería Don Pepe", "Cuit", "30123456789"));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, created.GetProperty("legalName").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("locality").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("province").ValueKind);

        // A value stored before this change is left alone by an update that still sends the old fields.
        OwnerExec("UPDATE customers SET legal_name = 'Old S.R.L.', locality = 'Old town', province = 'Old province' WHERE id = $1", id);
        var update = new
        {
            displayName = "Carnicería Don Pepe", legalName = "New S.A.", taxIdType = "Cuit", taxId = "30123456789",
            taxCondition = "ConsumidorFinal", locality = "New town", province = "New province", isEnabled = true,
        };
        var kept = await (await admin.PutAsJsonAsync($"/customers/{id}", update)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Company", kept.GetProperty("partyType").GetString());
        Assert.Equal("Old S.R.L.", OwnerScalar("SELECT legal_name FROM customers WHERE id = $1", id));
        Assert.Equal("Old town", OwnerScalar("SELECT locality FROM customers WHERE id = $1", id));
        Assert.Equal("Old province", OwnerScalar("SELECT province FROM customers WHERE id = $1", id));

        var changed = await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Pepe Gómez", taxIdType = "Cuit", taxId = "30123456789", taxCondition = "ConsumidorFinal",
            isEnabled = true, partyType = "Person",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("Person", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("partyType").GetString());

        await AssertFieldErrorAsync(await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Pepe Gómez", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true, partyType = "Nobody",
        }), "partyType");
    }

    // ------------------------------------------------------------------
    // Shared email rule (T3b)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomerAndContactEmails_FollowTheSharedRule_AndAreStoredTrimmed()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-cust-email");
        await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        await AssertFieldErrorAsync(await admin.PostAsJsonAsync("/customers", CustomerBody("Ana", email: "ana@mail")), "email");
        await AssertFieldErrorAsync(await admin.PostAsJsonAsync("/customers", CustomerBody("Ana", email: "ana@@mail.com")), "email");
        await AssertFieldErrorAsync(
            await admin.PostAsJsonAsync("/customers", CustomerBody("Ana", contacts: [new { firstName = "Luis", email = "luis@" }])), "contacts");

        var created = await CreateCustomerAsync(admin, CustomerBody(
            "Ana", email: "  a.b+c@mail.com.ar ", contacts: [new { firstName = "Luis", email = " Luis@Mail.com " }]));
        Assert.Equal("a.b+c@mail.com.ar", created.GetProperty("email").GetString());
        Assert.Equal("Luis@Mail.com", created.GetProperty("contacts")[0].GetProperty("email").GetString());

        var id = created.GetProperty("id").GetGuid();
        await AssertFieldErrorAsync(await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Ana", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true, email = "ana@-mail.com",
        }), "email");
    }

    [Fact]
    public async Task StaffUserEmail_OnCreate_FollowsTheSharedRule()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-staff");
        var (_, branchId, _) = await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        await AssertFieldErrorAsync(
            await admin.PostAsJsonAsync("/account/users", new CreateUserRequest("cajero@-mail.com", "cashier-password", ["cashier"], [branchId])), "email");
        await AssertFieldErrorAsync(
            await admin.PostAsJsonAsync("/account/users", new CreateUserRequest("cajero@mail", "cashier-password", ["cashier"], [branchId])), "email");
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync("/account/users", new CreateUserRequest(Unique("acff-cajero"), "cashier-password", ["cashier"], [branchId]))).StatusCode);
    }

    [Fact]
    public async Task SupplierEmail_FollowsTheSharedRule_OnCreateAndUpdate()
    {
        if (!_postgresAvailable) return;
        var email = Unique("acff-supplier");
        await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        await AssertFieldErrorAsync(await admin.PostAsJsonAsync("/suppliers", new { displayName = "Proveedor", email = "ventas@@mail.com" }), "email");

        var created = await admin.PostAsJsonAsync("/suppliers", new { displayName = "Proveedor", email = " ventas@mail.com " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("supplierId").GetGuid();
        Assert.Equal("ventas@mail.com", (await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{id}")).GetProperty("email").GetString());

        await AssertFieldErrorAsync(await admin.PutAsJsonAsync($"/suppliers/{id}", new { displayName = "Proveedor", email = "ventas@mail" }), "email");
    }
}

