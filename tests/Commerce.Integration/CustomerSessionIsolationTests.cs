using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-guest-ordering Phase 3 (tasks 3.3, 3.5, 3.6, 3.7): the
/// fourth <c>CustomerCookie</c> scheme, the <c>"Customer"</c> policy, and the
/// full negative-auth matrix — against a live Postgres instance
/// (`deploy/dev/compose.yaml`). If Postgres is not reachable, each test
/// reports the gap and returns without asserting pass/fail (existing
/// fixture convention).
/// </summary>
[Collection("Postgres")]
public sealed class CustomerSessionIsolationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerSessionIsolationTests(WebApplicationFactory<Program> factory)
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
            throw new InvalidOperationException("Could not locate repo root (Commerce.sln).");
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
        Apply("0005_password_recovery.sql");
        Apply("0006_role_taxonomy.sql");
        Apply("0007_platform_administration.sql", "__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        Apply("0008_customer_registry.sql");
        Apply("0009_catalog_and_pricing.sql");
        Apply("0010_guest_ordering.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE guest_order_verifications, price_import_rows, price_import_batches, " +
            "supplier_price_mappings, price_list_entries, price_lists, presentations, products, " +
            "customer_ordering_access, customers, password_reset_tokens, user_directory, users, " +
            "device_credentials, branches, organizations, platform_admins, audit_log RESTART IDENTITY CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private static WebApplicationFactoryClientOptions CookieClientOptions() => new()
    {
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    };

    /// <summary>
    /// Bootstraps a fresh org with a business-admin staff account
    /// (`ManageUsers`) via the real HTTP bootstrap flow.
    /// </summary>
    private async Task<(Guid OrganizationId, Guid BranchId, Guid AdminUserId)> BootstrapOrgAsync(
        HttpClient client, string adminEmail, string adminPassword)
    {
        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", adminEmail, adminPassword));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();

        // Bootstrap only CREATES the admin account — callers that go on to
        // use `client` against admin-gated routes (`/customers`,
        // `/account/users`) need it actually signed in.
        var signIn = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(adminEmail, adminPassword));
        signIn.EnsureSuccessStatusCode();

        return (organizationId, body!.BranchId, body.UserId);
    }

    /// <summary>
    /// Creates a registered-customer login: a `Customer` row, then a
    /// `CustomerId`-linked `UserAccount` through `/account/users` — the
    /// admin-provisioned path the spec requires (no self-registration).
    /// </summary>
    private async Task<Guid> CreateCustomerLoginAsync(
        HttpClient adminClient, string customerEmail, string customerPassword)
    {
        var createCustomerResponse = await adminClient.PostAsJsonAsync(
            "/customers",
            new CreateCustomerRequest("Retail", "Isolation Test Customer", null, "None", null, "ConsumidorFinal",
                null, customerEmail, null, null, null, null, null, null, null, null, null, null));
        createCustomerResponse.EnsureSuccessStatusCode();
        var createdCustomer = await createCustomerResponse.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var createUserResponse = await adminClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest(customerEmail, customerPassword, [], [], createdCustomer!.CustomerId));
        createUserResponse.EnsureSuccessStatusCode();

        return createdCustomer.CustomerId;
    }

    // --- 3.3: /customer/sign-in rejects a staff account ------------------

    [Fact]
    public async Task CustomerSignIn_WithStaffAccount_Returns401_SameAsBadPassword()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(adminClient, "3-3-staff@example.com", "staff-password");

        var response = await adminClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest("3-3-staff@example.com", "staff-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CustomerSignIn_RegisteredCustomer_Succeeds_IssuesCustomerScopedCookie()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(adminClient, "3-3-admin@example.com", "admin-password");
        await CreateCustomerLoginAsync(adminClient, "3-3-customer@example.com", "customer-password");

        var customerClient = _factory.CreateClient(CookieClientOptions());
        var response = await customerClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest("3-3-customer@example.com", "customer-password"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var customerCookie = Assert.Single(setCookies!, c => c.StartsWith("commerce.customer=", StringComparison.Ordinal));
        Assert.Contains("path=/customer", customerCookie, StringComparison.OrdinalIgnoreCase);
    }

    // --- 3.5 / 3.6: negative-auth matrix -----------------------------------

    /// <summary>
    /// Extracts the raw cookie pair (`name=value`) from a `Set-Cookie` header
    /// value so it can be replayed manually on a request outside its
    /// declared `Path` — proving the SCHEME boundary itself, independent of
    /// the cookie-jar's own path-based transmission behavior (the design's
    /// stated "two independent reasons it cannot reach staff endpoints").
    /// </summary>
    private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

    /// <summary>
    /// One REAL, actually-mapped route per staff group (a bare `GET /orders`
    /// etc. is not a route at all in several groups and 404s regardless of
    /// auth — these are chosen so a 404 can only mean "no route", never a
    /// false pass for the auth boundary under test).
    /// </summary>
    public static IEnumerable<object[]> StaffRoutes()
    {
        yield return new object[] { HttpMethod.Get, $"/orders/{Guid.NewGuid()}" };
        yield return new object[] { HttpMethod.Get, "/catalog/products" };
        yield return new object[] { HttpMethod.Get, "/customers" };
        yield return new object[] { HttpMethod.Get, "/pricing/price-lists" };
        yield return new object[] { HttpMethod.Post, "/account/users" };
        yield return new object[] { HttpMethod.Get, "/platform/organizations" };
    }

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    public async Task CustomerCookie_ForciblyPresented_OnStaffEndpoint_Returns401(HttpMethod method, string staffPath)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var slug = staffPath.Replace('/', '_').Replace(':', '_');
        var adminClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(adminClient, $"3-5-admin-{slug}@example.com", "admin-password");
        var customerEmail = $"3-5-customer-{slug}@example.com";
        await CreateCustomerLoginAsync(adminClient, customerEmail, "customer-password");

        var anonymousSignInClient = _factory.CreateClient();
        var signIn = await anonymousSignInClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest(customerEmail, "customer-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.True(signIn.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var customerCookiePair = ExtractCookiePair(
            Assert.Single(setCookies!, c => c.StartsWith("commerce.customer=", StringComparison.Ordinal)));

        // Bare client (no CookieContainer path filtering, no auto-redirect):
        // the cookie is attached MANUALLY, bypassing Path-based
        // non-transmission, so a 401/403/redirect-challenge here proves the
        // "Customer" policy's scheme restriction is the actual barrier, not
        // merely that the browser never sent the cookie. Groups without an
        // explicit `OnRedirectToLogin` override (the default staff cookie's
        // own groups) challenge with a 302 to the framework's default login
        // path instead of a bare 401 — AllowAutoRedirect=false stops that
        // from resolving into an unrelated static-file 404 in a test host
        // that ships no SPA bundle.
        var probeClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(method, staffPath);
        request.Headers.Add("Cookie", customerCookiePair);
        if (method == HttpMethod.Post)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(new { });
        }
        var response = await probeClient.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected an authentication rejection for {method} {staffPath}, got {response.StatusCode}.");
    }

    [Fact]
    public async Task StaffCookie_ForciblyPresented_OnCustomerSignOut_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await BootstrapOrgAsync(_factory.CreateClient(CookieClientOptions()), "3-6-staff@example.com", "staff-password");

        var staffClient = _factory.CreateClient(CookieClientOptions());
        var staffSignIn = await staffClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("3-6-staff@example.com", "staff-password"));
        Assert.Equal(HttpStatusCode.OK, staffSignIn.StatusCode);
        Assert.True(staffSignIn.Headers.TryGetValues("Set-Cookie", out var setCookies));
        // The staff scheme uses the framework default cookie name
        // (".AspNetCore.Cookies") — the only Set-Cookie header /account/sign-in
        // issues.
        var staffCookiePair = ExtractCookiePair(setCookies!.First());

        var probeClient = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/customer/sign-out");
        request.Headers.Add("Cookie", staffCookiePair);
        var response = await probeClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- 3.7: no /register-shaped route exists ------------------------------

    [Fact]
    public void MappedRouteTable_ContainsNoRegisterShapedRoute()
    {
        using var scope = _factory.Services.CreateScope();
        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        Assert.NotEmpty(routePatterns);
        Assert.DoesNotContain(routePatterns, p => p.Contains("register", StringComparison.OrdinalIgnoreCase));
    }
}
