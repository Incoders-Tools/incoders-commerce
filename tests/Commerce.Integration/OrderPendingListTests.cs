using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Commerce.Domain.Ordering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Phase 8 follow-up C (commerce-guest-ordering verify-report.md WARNING 3):
/// <see cref="Order.DispatchRank"/> was implemented and unit-tested in
/// isolation (<c>OrderOriginTests</c>) but never exercised end to end,
/// because no endpoint read orders back in a priority-ordered way. This
/// submits a GUEST order first, then a REGISTERED order (submission order
/// deliberately reversed from dispatch priority), and proves
/// <c>GET /orders/pending</c> returns the registered order FIRST.
/// </summary>
[Collection("Postgres")]
public sealed class OrderPendingListTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly Guid _organizationId = Guid.NewGuid();
    private readonly Guid _destinationBranchId = Guid.NewGuid();
    private readonly WebApplicationFactory<Program> _factory;

    public OrderPendingListTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            // The public guest surface's org/branch target is set to the
            // SAME organization this test bootstraps as staff/registered,
            // so both the guest order and the registered order land in one
            // organization's pending list.
            builder.UseSetting("GuestOrdering:OrganizationId", _organizationId.ToString());
            builder.UseSetting("GuestOrdering:BranchId", _destinationBranchId.ToString());
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
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
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

        // B7 U5: guards against the shared/accumulating commerce_test
        // database hazard (see PaymentEndpointTests) — 0009 own
        // price_lists_one_default is ORG-scoped.
        using (var truncatePricingCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_list_entries, price_lists CASCADE", owner))
        {
            try { truncatePricingCmd.ExecuteNonQuery(); } catch (PostgresException) { /* first run: tables do not exist yet */ }
        }

        Apply("0001_init_rls.sql", "__APP_RUNTIME_PASSWORD__", "dev-only-password");
        Apply("0002_users.sql");
        Apply("0003_organizations_branches.sql");
        Apply("0021_branch_codes.sql");
        Apply("0004_device_credentials.sql");
        Apply("0005_password_recovery.sql");
        Apply("0006_role_taxonomy.sql");
        Apply("0007_platform_administration.sql", "__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        Apply("0008_customer_registry.sql");
        Apply("0009_catalog_and_pricing.sql");
        Apply("0010_guest_ordering.sql");
        Apply("0025_orders.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE order_lines, orders, guest_order_verifications, price_import_rows, price_import_batches, " +
            "supplier_price_mappings, price_list_entries, price_lists, presentations, products, " +
            "customer_ordering_access, customers, password_reset_tokens, user_directory, users, " +
            "device_credentials, branches, organizations RESTART IDENTITY CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private static WebApplicationFactoryClientOptions CookieClientOptions() => new()
    {
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    };

    private async Task<HttpClient> BootstrapOrgAsync(string adminEmail, string adminPassword, bool seedDestinationBranch = true)
    {
        var adminClient = _factory.CreateClient(CookieClientOptions());
        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var token = registry.Issue(_organizationId);

        var response = await adminClient.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(_organizationId, token, "Org " + _organizationId, "HQ", adminEmail, adminPassword));
        response.EnsureSuccessStatusCode();

        // Orders are stored against a real branch of the organization (an unknown destination is a denial).
        if (seedDestinationBranch)
        await using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var seed = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Order destination')", owner);
            seed.Parameters.AddWithValue(_destinationBranchId);
            seed.Parameters.AddWithValue(_organizationId);
            await seed.ExecuteNonQueryAsync();
        }

        var signIn = await adminClient.PostAsJsonAsync("/account/sign-in", new SignInRequest(adminEmail, adminPassword));
        signIn.EnsureSuccessStatusCode();

        return adminClient;
    }

    private async Task<Guid> SubmitGuestOrderAsync(string email)
    {
        var (orderId, response) = await SubmitGuestOrderRawAsync(email);
        response.EnsureSuccessStatusCode();
        return orderId;
    }

    private async Task<(Guid OrderId, HttpResponseMessage Response)> SubmitGuestOrderRawAsync(string email)
    {
        var client = _factory.CreateClient();

        var verificationResponse = await client.PostAsJsonAsync(
            "/public/guest-orders/verification", new GuestVerificationRequest("30111222333", email));
        verificationResponse.EnsureSuccessStatusCode();
        var verification = await verificationResponse.Content.ReadFromJsonAsync<GuestVerificationRequestedResponse>();

        var codeResponse = await client.GetAsync(
            $"/internal/test-seed/guest-verification-code?contactAddress={Uri.EscapeDataString(email)}");
        codeResponse.EnsureSuccessStatusCode();
        var code = await codeResponse.Content.ReadFromJsonAsync<TestSeedGuestVerificationCodeResponse>();

        var confirmResponse = await client.PostAsJsonAsync(
            "/public/guest-orders/verification/confirm",
            new GuestVerificationConfirmRequest(verification!.VerificationId, code!.Code));
        confirmResponse.EnsureSuccessStatusCode();

        var orderId = Guid.NewGuid();
        var submitResponse = await client.PostAsJsonAsync(
            "/public/guest-orders",
            new SubmitGuestOrderRequest(
                orderId, verification.VerificationId, "30111222333", email, "Guest Buyer", null, [], Guid.NewGuid()));
        return (orderId, submitResponse);
    }

    private async Task<Guid> SubmitRegisteredOrderAsync(HttpClient adminClient, string customerEmail, string customerPassword)
    {
        var createCustomerResponse = await adminClient.PostAsJsonAsync(
            "/customers",
            new CreateCustomerRequest("Retail", "Pending-List Test Customer", null, "None", null, "ConsumidorFinal",
                null, customerEmail, null, null, null, null, null, null, null, null, null, null));
        createCustomerResponse.EnsureSuccessStatusCode();
        var createdCustomer = await createCustomerResponse.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var createUserResponse = await adminClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest(customerEmail, customerPassword, [], [], createdCustomer!.CustomerId));
        createUserResponse.EnsureSuccessStatusCode();

        var customerClient = _factory.CreateClient(CookieClientOptions());
        var signIn = await customerClient.PostAsJsonAsync(
            "/customer/sign-in", new CustomerSignInRequest(customerEmail, customerPassword));
        signIn.EnsureSuccessStatusCode();

        var orderId = Guid.NewGuid();
        var submitResponse = await customerClient.PostAsJsonAsync(
            "/customer/orders",
            new { orderId, lines = Array.Empty<object>(), correlationId = Guid.NewGuid() });
        submitResponse.EnsureSuccessStatusCode();

        return orderId;
    }

    [Fact]
    public async Task GetOrdersPending_GuestSubmittedFirst_RegisteredSubmittedSecond_ReturnsRegisteredFirst()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = await BootstrapOrgAsync("pending-list-admin@example.com", "admin-password");

        // Submission order is deliberately GUEST then REGISTERED — the
        // opposite of the expected dispatch-priority order.
        var guestOrderId = await SubmitGuestOrderAsync("pending-list-guest@example.com");
        var registeredOrderId = await SubmitRegisteredOrderAsync(
            adminClient, "pending-list-customer@example.com", "customer-password");

        var pendingResponse = await adminClient.GetAsync("/orders/pending");
        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);

        var pending = await pendingResponse.Content.ReadFromJsonAsync<List<Order>>();
        Assert.NotNull(pending);
        Assert.Equal(2, pending!.Count);

        // DispatchRank sorts registered (0) before guest (1) regardless of
        // submission order.
        Assert.Equal(registeredOrderId, pending[0].OrderId);
        Assert.Equal(OrderOrigin.RegisteredCustomer, pending[0].Origin);
        Assert.Equal(guestOrderId, pending[1].OrderId);
        Assert.Equal(OrderOrigin.Guest, pending[1].Origin);
    }

    [Fact]
    public async Task StoredOrders_ArePendingListedWithTheirNumbers_AndSurviveARestartOfTheApi()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminClient = await BootstrapOrgAsync("numbered-admin@example.com", "admin-password");
        var guestOrderId = await SubmitGuestOrderAsync("numbered-guest@example.com");
        var registeredOrderId = await SubmitRegisteredOrderAsync(adminClient, "numbered-customer@example.com", "customer-password");

        // (The HQ branch is 01, the destination seeded after it is 02.) A second host over the same database stands for the API after a restart.
        using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
        });
        var restartedAdmin = restarted.CreateClient(CookieClientOptions());
        var signIn = await restartedAdmin.PostAsJsonAsync("/account/sign-in", new SignInRequest("numbered-admin@example.com", "admin-password"));
        signIn.EnsureSuccessStatusCode();

        var pending = await restartedAdmin.GetFromJsonAsync<List<Order>>("/orders/pending");

        Assert.Equal([registeredOrderId, guestOrderId], pending!.Select(o => o.OrderId));
        Assert.Equal(["P02-W-2", "P02-W-1"], pending.Select(o => o.OrderNumber!.Value.Format()));

        var found = await restartedAdmin.GetFromJsonAsync<Order>($"/orders/{guestOrderId}");
        Assert.Equal("P02-W-1", found!.OrderNumber!.Value.Format());
    }

    [Fact]
    public async Task GuestOrder_ToAnUnknownDestinationBranch_IsADeniedOutcome_NotAServerError()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await BootstrapOrgAsync("unknown-branch-admin@example.com", "admin-password", seedDestinationBranch: false);

        var (_, response) = await SubmitGuestOrderRawAsync("unknown-branch-guest@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("destination-branch-not-found", body);
    }
}
