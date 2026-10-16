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
/// Customer contacts: the `contacts` array on the customer JSON, replace-set
/// semantics on create/update (ids kept when sent, omitted array on PUT keeps
/// the stored set) and the customer search matching contact names.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerContactsEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerContactsEndpointTests(WebApplicationFactory<Program> factory)
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

    private async Task BootstrapAsync(string email)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static object Body(string displayName, object? contacts = null) => new
    {
        customerKind = "Retail", displayName, taxIdType = "None", taxCondition = "ConsumidorFinal", contacts,
    };

    private static object PutBody(string displayName, object? contacts = null, DateTimeOffset? expected = null) => new
    {
        displayName, taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true, contacts, expectedUpdatedAtUtc = expected,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/customers", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("customerId").GetGuid();
    }

    private static string[] FirstNames(JsonElement customer) =>
        customer.GetProperty("contacts").EnumerateArray().Select(c => c.GetProperty("firstName").GetString()!).ToArray();

    [Fact]
    public async Task Customer_IsCreatedWithContacts_AndReturnsThemInOrder()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("contacts-create@example.com");
        var admin = await SignInAsync("contacts-create@example.com");

        var id = await CreateAsync(admin, Body("Bar Pepe", new object[]
        {
            new { firstName = "  Ana ", lastName = "Gómez", phone = "11 5555-0000", email = "ana@example.com", role = "Compras", isPrimary = true, sortOrder = 2 },
            new { firstName = "Beto", sortOrder = 1 },
        }));

        var customer = await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}");
        var contacts = customer.GetProperty("contacts").EnumerateArray().ToArray();
        Assert.Equal(["Beto", "Ana"], FirstNames(customer)); // by sortOrder
        Assert.Equal("Ana", contacts[1].GetProperty("firstName").GetString());
        Assert.Equal("Gómez", contacts[1].GetProperty("lastName").GetString());
        Assert.Equal("11 5555-0000", contacts[1].GetProperty("phone").GetString());
        Assert.Equal("ana@example.com", contacts[1].GetProperty("email").GetString());
        Assert.Equal("Compras", contacts[1].GetProperty("role").GetString());
        Assert.True(contacts[1].GetProperty("isPrimary").GetBoolean());
        Assert.False(contacts[0].GetProperty("isPrimary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, contacts[0].GetProperty("lastName").ValueKind);
        Assert.NotEqual(Guid.Empty, contacts[0].GetProperty("id").GetGuid());
        Assert.False(customer.TryGetProperty("contactName", out _)); // the single free-text field is gone

        // A customer created without contacts reports an empty array, never null.
        var plain = await CreateAsync(admin, Body("Sin contactos"));
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/customers/{plain}")).GetProperty("contacts").GetArrayLength());
    }

    [Fact]
    public async Task CustomerUpdate_ReplacesTheContactSet_KeepingSentIds_AndKeepsItWhenOmitted()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("contacts-put@example.com");
        var admin = await SignInAsync("contacts-put@example.com");
        var id = await CreateAsync(admin, Body("Bar Pepe", new object[]
        {
            new { firstName = "Ana", isPrimary = true },
            new { firstName = "Beto" },
            new { firstName = "Carla" },
        }));
        var before = (await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}")).GetProperty("contacts").EnumerateArray().ToArray();
        var (ana, beto) = (before[0].GetProperty("id").GetGuid(), before[1].GetProperty("id").GetGuid());
        var newId = Guid.NewGuid();

        // Ana is edited and loses the primary flag to Beto; Carla is dropped; Dora (client-chosen id) is new.
        var put = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe", new object[]
        {
            new { id = ana, firstName = "Ana María", lastName = "Gómez", phone = "123" },
            new { id = beto, firstName = "Beto", isPrimary = true },
            new { id = newId, firstName = "Dora" },
        }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await put.Content.ReadFromJsonAsync<JsonElement>();
        var contacts = after.GetProperty("contacts").EnumerateArray().ToArray();
        Assert.Equal(["Ana María", "Beto", "Dora"], FirstNames(after));
        Assert.Equal([ana, beto, newId], contacts.Select(c => c.GetProperty("id").GetGuid()).ToArray()); // ids preserved
        Assert.Equal([false, true, false], contacts.Select(c => c.GetProperty("isPrimary").GetBoolean()).ToArray());
        Assert.Equal("123", contacts[0].GetProperty("phone").GetString());

        // No `contacts` property (the POS): the stored set is untouched.
        var legacy = await admin.PutAsJsonAsync($"/customers/{id}", new
        {
            displayName = "Bar Pepe 2", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true,
        });
        Assert.Equal(["Ana María", "Beto", "Dora"], FirstNames(await legacy.Content.ReadFromJsonAsync<JsonElement>()));

        // An empty array clears them.
        var cleared = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 2", Array.Empty<object>()));
        Assert.Equal(0, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("contacts").GetArrayLength());
    }

    [Fact]
    public async Task Contacts_AreValidated_AndOneCustomerCannotStealAnotherOnesContact()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("contacts-valid-a@example.com");
        await BootstrapAsync("contacts-valid-b@example.com");
        var adminA = await SignInAsync("contacts-valid-a@example.com");
        var adminB = await SignInAsync("contacts-valid-b@example.com");
        var customerA = await CreateAsync(adminA, Body("A", new object[] { new { firstName = "Ana" } }));
        var other = await CreateAsync(adminA, Body("A2", new object[] { new { firstName = "Otra" } }));
        var foreignOrgCustomer = await CreateAsync(adminB, Body("B", new object[] { new { firstName = "Beto" } }));
        Guid ContactOf(JsonElement c) => c.GetProperty("contacts")[0].GetProperty("id").GetGuid();
        var anaId = ContactOf(await adminA.GetFromJsonAsync<JsonElement>($"/customers/{customerA}"));
        var otherId = ContactOf(await adminA.GetFromJsonAsync<JsonElement>($"/customers/{other}"));
        var betoId = ContactOf(await adminB.GetFromJsonAsync<JsonElement>($"/customers/{foreignOrgCustomer}"));

        async Task AssertBadRequest(string expectedField, object contacts, HttpClient? client = null, Guid? target = null)
        {
            var response = await (client ?? adminA).PutAsJsonAsync($"/customers/{target ?? customerA}", PutBody("A", contacts));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectedField, await response.Content.ReadAsStringAsync());
        }

        await AssertBadRequest("contacts", new object[] { new { firstName = "   " } });
        await AssertBadRequest("contacts", new object[] { new { lastName = "Sin nombre" } });
        await AssertBadRequest("contacts", new object[] { new { firstName = "Uno", isPrimary = true }, new { firstName = "Dos", isPrimary = true } });
        await AssertBadRequest("contacts", new object[] { new { id = anaId, firstName = "Ana" }, new { id = anaId, firstName = "Ana otra vez" } });
        await AssertBadRequest("contacts", new object[] { new { id = otherId, firstName = "Robada" } });   // another customer, same organization
        await AssertBadRequest("contacts", new object[] { new { id = betoId, firstName = "Robado" } });    // another organization
        await AssertBadRequest("contacts", Enumerable.Range(0, 51).Select(i => new { firstName = "C" + i }).ToArray());

        // Nothing above changed anything.
        Assert.Equal(["Ana"], FirstNames(await adminA.GetFromJsonAsync<JsonElement>($"/customers/{customerA}")));
        Assert.Equal(["Otra"], FirstNames(await adminA.GetFromJsonAsync<JsonElement>($"/customers/{other}")));
        Assert.Equal(["Beto"], FirstNames(await adminB.GetFromJsonAsync<JsonElement>($"/customers/{foreignOrgCustomer}")));

        var badCreate = await adminA.PostAsJsonAsync("/customers", Body("Mal", new object[] { new { firstName = "" } }));
        Assert.Equal(HttpStatusCode.BadRequest, badCreate.StatusCode);
        Assert.Contains("contacts", await badCreate.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CustomerUpdate_WithAStaleToken_Returns409_AndLeavesTheContactsUntouched()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("contacts-occ@example.com");
        var admin = await SignInAsync("contacts-occ@example.com");
        var id = await CreateAsync(admin, Body("Bar Pepe", new object[] { new { firstName = "Ana" } }));
        var token = (await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}")).GetProperty("updatedAtUtc").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 2"))).StatusCode); // moves the token

        var stale = await admin.PutAsJsonAsync($"/customers/{id}", PutBody("Bar Pepe 3", new object[] { new { firstName = "Perdido" } }, token));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(["Ana"], FirstNames(await admin.GetFromJsonAsync<JsonElement>($"/customers/{id}")));
    }

    [Fact]
    public async Task Customers_ListCarriesContacts_AndSearchMatchesContactFirstAndLastNames()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("contacts-search@example.com");
        var admin = await SignInAsync("contacts-search@example.com");
        await CreateAsync(admin, Body("Zeta Resto", new object[] { new { firstName = "Marta", lastName = "Pérez", phone = "5551234" } }));
        await CreateAsync(admin, Body("José Bar", new object[]
        {
            new { firstName = "Ñoño", lastName = "Quiroga", isPrimary = true }, new { firstName = "Luis" },
        }));
        await CreateAsync(admin, Body("Alfa Bar"));

        async Task<JsonElement> List(string qs) => await admin.GetFromJsonAsync<JsonElement>("/customers" + qs);
        static string[] Names(JsonElement list) => list.EnumerateArray().Select(e => e.GetProperty("displayName").GetString()!).ToArray();

        var all = await List("");
        Assert.Equal(["Alfa Bar", "José Bar", "Zeta Resto"], Names(all));
        Assert.Equal([0, 2, 1], all.EnumerateArray().Select(c => c.GetProperty("contacts").GetArrayLength()).ToArray());

        Assert.Equal(["José Bar"], Names(await List("?search=NONO")));        // first name, ñ folded
        Assert.Equal(["José Bar"], Names(await List("?search=quiroga")));      // last name
        Assert.Equal(["José Bar"], Names(await List("?search=luis")));         // a secondary contact
        Assert.Equal(["Zeta Resto"], Names(await List("?search=PEREZ")));      // accent folded
        Assert.Empty(Names(await List("?search=ana@")));                       // email is not searched
        Assert.Empty(Names(await List("?search=5551234")));                    // phone is not searched
        Assert.Equal(["Alfa Bar", "José Bar"], Names(await List("?search=bar"))); // name search unchanged
    }
}
