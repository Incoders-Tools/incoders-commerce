using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity task 4.5: `GET /device/customers/sync`
/// — device bearer required, org/branch come from the STORED
/// `device_credentials` row (never the query), `since` filters to changed
/// rows only, and a customer disabled after the cursor appears in
/// `disabledIds`. If Postgres is not reachable, these tests report the gap
/// clearly and return without asserting pass/fail, matching the existing
/// fixture convention (`PostgresTestFixture`).
/// </summary>
[Collection("Postgres")]
public sealed class CustomerSyncTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerSyncTests(WebApplicationFactory<Program> factory)
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
        Apply("0005_password_recovery.sql");
        Apply("0006_role_taxonomy.sql");
        Apply("0007_platform_administration.sql", "__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        Apply("0008_customer_registry.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", connection);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedBranchAsync(Guid orgId, Guid branchId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", connection);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> IssueDeviceTokenAsync(Guid orgId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return issued.PlaintextToken;
    }

    private async Task<Guid> CreateCustomerAsync(Guid orgId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var customerStore = scope.ServiceProvider.GetRequiredService<PostgresCustomerStore>();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var customer = new NewCustomer(
            customerId, CustomerKind.Retail, displayName, LegalName: null, TaxIdType.None, TaxId: null,
            TaxCondition.ConsumidorFinal, Phone: null, Email: null, AddressStreet: null, AddressNumber: null,
            Neighborhood: null, Locality: null, Province: null, PostalCode: null, DeliveryNotes: null,
            DiscountPercentage: null, PaymentTerms: null, Notes: null, actorId);
        await customerStore.CreateAsync(new CloudTenantScope(orgId), customer, "org-user", actorId, CancellationToken.None);
        return customerId;
    }

    private static HttpRequestMessage BuildRequest(string path, string? deviceToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (deviceToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        }
        return request;
    }

    [Fact]
    public async Task Sync_NoDeviceBearer_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/customers/sync?since={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_ValidDeviceBearer_ReturnsChangedCustomers_ScopedToStoredOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var customerId = await CreateCustomerAsync(orgId, "Synced Customer");

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/customers/sync?since={Uri.EscapeDataString(since.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CustomerSyncResponse>();
        Assert.Contains(body!.Customers, c => c.CustomerId == customerId);
    }

    [Fact]
    public async Task Sync_SinceCursorAfterCreation_ExcludesUnchangedCustomer()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        await CreateCustomerAsync(orgId, "Old Customer");
        await Task.Delay(50);
        var cursor = DateTimeOffset.UtcNow;

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/customers/sync?since={Uri.EscapeDataString(cursor.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CustomerSyncResponse>();
        Assert.Empty(body!.Customers);
    }

    [Fact]
    public async Task Sync_CustomerDisabledAfterCursor_AppearsInDisabledIds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        await SeedOrganizationAsync(orgId);
        await SeedBranchAsync(orgId, branchId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var customerId = await CreateCustomerAsync(orgId, "About To Be Disabled");
        await Task.Delay(50);
        var cursor = DateTimeOffset.UtcNow;
        await Task.Delay(50);

        await using (var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "UPDATE customers SET is_enabled = false, updated_at_utc = now() WHERE id = $1", connection);
            cmd.Parameters.AddWithValue(customerId);
            await cmd.ExecuteNonQueryAsync();
        }

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/customers/sync?since={Uri.EscapeDataString(cursor.ToString("O"))}", deviceToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CustomerSyncResponse>();
        Assert.Contains(customerId, body!.DisabledIds);
        Assert.DoesNotContain(body.Customers, c => c.CustomerId == customerId);
    }

    [Fact]
    public async Task Sync_OrgAToken_NeverSeesOrgBCustomers()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        await SeedBranchAsync(orgAId, branchAId);
        var deviceTokenA = await IssueDeviceTokenAsync(orgAId, branchAId);

        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        await CreateCustomerAsync(orgBId, "Org B Customer");

        var client = _factory.CreateClient();
        var response = await client.SendAsync(
            BuildRequest($"/device/customers/sync?since={Uri.EscapeDataString(since.ToString("O"))}", deviceTokenA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CustomerSyncResponse>();
        Assert.Empty(body!.Customers);
    }
}
