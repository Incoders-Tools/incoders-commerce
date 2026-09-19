using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Gap-closing follow-up unit (found during commerce-guest-ordering Unit 6's
/// independent verification, NOT a task that existed in the original Phase
/// 1-6 breakdown): design.md's "Registered customer order" data flow and
/// `src/Commerce.Web/src/api/customerSession.ts`'s `submitCustomerOrder`
/// both document/target `POST /customer/orders`, but no Phase 1-5 task ever
/// created it. Covers the endpoint's happy path plus the negative-auth
/// matrix, mirroring <see cref="CustomerSessionIsolationTests"/>'s fixture
/// conventions.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerOrderSubmissionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerOrderSubmissionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            // GuestOrderTarget is the ONE existing org/branch resolution point
            // in this host (design.md "Org/branch resolution point") — reused
            // here purely for the destination branch id a registered
            // customer's self-service order needs to carry, independent of
            // the customer's OWN organization (resolved separately from the
            // customer cookie's org_id claim). Any configured value works;
            // this test does not assert on it.
            builder.UseSetting("GuestOrdering:OrganizationId", Guid.NewGuid().ToString());
            builder.UseSetting("GuestOrdering:BranchId", Guid.NewGuid().ToString());
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

    private async Task<Guid> BootstrapOrgAsync(HttpClient client, string adminEmail, string adminPassword)
    {
        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", adminEmail, adminPassword));
        response.EnsureSuccessStatusCode();

        var signIn = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(adminEmail, adminPassword));
        signIn.EnsureSuccessStatusCode();

        return organizationId;
    }

    private async Task<Guid> CreateCustomerLoginAsync(HttpClient adminClient, string customerEmail, string customerPassword)
    {
        var createCustomerResponse = await adminClient.PostAsJsonAsync(
            "/customers",
            new CreateCustomerRequest("Retail", "Self-Service Test Customer", null, "None", null, "ConsumidorFinal",
                null, customerEmail, null, null, null, null, null, null, null, null, null, null));
        createCustomerResponse.EnsureSuccessStatusCode();
        var createdCustomer = await createCustomerResponse.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var createUserResponse = await adminClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest(customerEmail, customerPassword, [], [], createdCustomer!.CustomerId));
        createUserResponse.EnsureSuccessStatusCode();

        return createdCustomer.CustomerId;
    }

    private async Task<HttpClient> SignedInCustomerClientAsync(HttpClient adminClient, string customerEmail, string customerPassword)
    {
        await CreateCustomerLoginAsync(adminClient, customerEmail, customerPassword);

        var customerClient = _factory.CreateClient(CookieClientOptions());
        var signIn = await customerClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest(customerEmail, customerPassword));
        signIn.EnsureSuccessStatusCode();
        return customerClient;
    }

    // --- Happy path: a signed-in registered customer submits their own order ---

    [Fact]
    public async Task PostCustomerOrders_ValidSession_SubmitsOrder_ReturnsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(adminClient, "gap-admin@example.com", "admin-password");
        var customerClient = await SignedInCustomerClientAsync(adminClient, "gap-customer@example.com", "customer-password");

        // Zero-line order: CloudOrderSubmissionService.ResolveLinesAsync never
        // touches pricing at all for an empty line set (existing regression-
        // guard convention, see CloudOrderSubmissionService remarks) — this
        // test proves the SESSION/AUTHORIZATION/ORIGIN-STAMPING wiring, not
        // catalog/pricing resolution, which is already covered elsewhere.
        var response = await customerClient.PostAsJsonAsync(
            "/customer/orders",
            new { orderId = Guid.NewGuid(), lines = Array.Empty<object>(), correlationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await response.Content.ReadFromJsonAsync<OrderSubmissionOutcome>();
        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome!.Status);
    }

    // NOTE (scope decision, documented): a dedicated "disabled customer"
    // negative test is NOT added here. `SubmitForCustomerSessionAsync`
    // reuses the EXACT SAME `_customerStore.FindAsync` +
    // `customer.IsEnabled` check, in the same relative position, that
    // `SubmitAsync`'s "customer-disabled" denial already exercises — no new
    // code path is introduced, only a new caller of it, so duplicating that
    // coverage here would test the reused check twice rather than the new
    // wiring.

    // --- Negative-auth matrix ------------------------------------------------

    [Fact]
    public async Task PostCustomerOrders_NoSession_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var anonymousClient = _factory.CreateClient(CookieClientOptions());

        var response = await anonymousClient.PostAsJsonAsync(
            "/customer/orders",
            new { orderId = Guid.NewGuid(), lines = Array.Empty<object>(), correlationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string ExtractCookiePair(string setCookieHeader) => setCookieHeader.Split(';')[0];

    [Fact]
    public async Task PostCustomerOrders_StaffCookie_ForciblyPresented_CannotSubstitute_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var staffClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(staffClient, "gap-staff-isolation@example.com", "staff-password");

        var staffSignIn = await staffClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("gap-staff-isolation@example.com", "staff-password"));
        Assert.Equal(HttpStatusCode.OK, staffSignIn.StatusCode);
        Assert.True(staffSignIn.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var staffCookiePair = ExtractCookiePair(setCookies!.First());

        // Bare client, cookie attached manually (mirrors
        // CustomerSessionIsolationTests' probe convention): proves the
        // "Customer" policy's scheme restriction is the real barrier, not
        // merely that the browser never sent a mismatched-path cookie.
        var probeClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Post, "/customer/orders")
        {
            Content = JsonContent.Create(new { orderId = Guid.NewGuid(), lines = Array.Empty<object>(), correlationId = Guid.NewGuid() }),
        };
        request.Headers.Add("Cookie", staffCookiePair);
        var response = await probeClient.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected an authentication rejection, got {response.StatusCode}.");
    }
}

/// <summary>
/// Phase 8 follow-up A (verify-report.md WARNING 1): registered-customer
/// self-service ordering must not structurally depend on whether the public
/// guest surface is configured (ADR-009: order-origin channels are
/// independent). This fixture deliberately OMITS `GuestOrdering:*` — unlike
/// <see cref="CustomerOrderSubmissionTests"/>'s fixture, which always sets
/// it — to prove `/customer/orders` stays reachable for a signed-in customer
/// regardless.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerOrderSubmissionWithoutGuestConfigTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerOrderSubmissionWithoutGuestConfigTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            // GuestOrdering:* deliberately absent — the public guest surface
            // stays unconfigured/disabled for this whole fixture.
        });

        if (_postgresAvailable)
        {
            CustomerOrderSubmissionTestsMigrations.ApplyMigrationsAndReset();
        }
    }

    public void Dispose() => _factory.Dispose();

    private static WebApplicationFactoryClientOptions CookieClientOptions() => new()
    {
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    };

    private async Task<Guid> BootstrapOrgAsync(HttpClient client, string adminEmail, string adminPassword)
    {
        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", adminEmail, adminPassword));
        response.EnsureSuccessStatusCode();

        var signIn = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(adminEmail, adminPassword));
        signIn.EnsureSuccessStatusCode();

        return organizationId;
    }

    private async Task<Guid> CreateCustomerLoginAsync(HttpClient adminClient, string customerEmail, string customerPassword)
    {
        var createCustomerResponse = await adminClient.PostAsJsonAsync(
            "/customers",
            new CreateCustomerRequest("Retail", "Self-Service Test Customer", null, "None", null, "ConsumidorFinal",
                null, customerEmail, null, null, null, null, null, null, null, null, null, null));
        createCustomerResponse.EnsureSuccessStatusCode();
        var createdCustomer = await createCustomerResponse.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var createUserResponse = await adminClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest(customerEmail, customerPassword, [], [], createdCustomer!.CustomerId));
        createUserResponse.EnsureSuccessStatusCode();

        return createdCustomer.CustomerId;
    }

    private async Task<HttpClient> SignedInCustomerClientAsync(HttpClient adminClient, string customerEmail, string customerPassword)
    {
        await CreateCustomerLoginAsync(adminClient, customerEmail, customerPassword);

        var customerClient = _factory.CreateClient(CookieClientOptions());
        var signIn = await customerClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest(customerEmail, customerPassword));
        signIn.EnsureSuccessStatusCode();
        return customerClient;
    }

    /// <summary>
    /// RED (before the fix): with `GuestOrdering:*` absent,
    /// `MapCustomerSessionEndpoints` never mapped `/customer/orders` at all
    /// — a signed-in customer got a bare route-level 404, indistinguishable
    /// from a typo'd URL. GREEN (after): the route is always mapped; a
    /// signed-in customer without a configured branch target gets an
    /// explicit 503 (a handled, documented degradation), never a 404 — the
    /// channel itself is reachable regardless of the guest surface's config.
    /// </summary>
    [Fact]
    public async Task PostCustomerOrders_ValidSession_GuestConfigAbsent_IsReachable_NeverRouteLevel404()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(adminClient, "decoupled-admin@example.com", "admin-password");
        var customerClient = await SignedInCustomerClientAsync(adminClient, "decoupled-customer@example.com", "customer-password");

        var response = await customerClient.PostAsJsonAsync(
            "/customer/orders",
            new { orderId = Guid.NewGuid(), lines = Array.Empty<object>(), correlationId = Guid.NewGuid() });

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// Auth still runs identically regardless of guest config: an anonymous
    /// caller gets 401, never a route-level 404 either.
    /// </summary>
    [Fact]
    public async Task PostCustomerOrders_NoSession_GuestConfigAbsent_Returns401_NotNotFound()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var anonymousClient = _factory.CreateClient(CookieClientOptions());

        var response = await anonymousClient.PostAsJsonAsync(
            "/customer/orders",
            new { orderId = Guid.NewGuid(), lines = Array.Empty<object>(), correlationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Extracted migration/reset helper shared by both fixtures in this file so
/// neither one duplicates the other's private static method.
/// </summary>
internal static class CustomerOrderSubmissionTestsMigrations
{
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

    public static void ApplyMigrationsAndReset()
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
}
