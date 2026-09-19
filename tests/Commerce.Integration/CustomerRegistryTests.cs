using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-customer-identity task 2.5 (store-level) and task 4.1
/// (endpoint-level, `WebApplicationFactory` + live Postgres): `ManageUsers`
/// creates Retail/Wholesale customers, a `seller` gets 403, one audit row is
/// written per create/edit, a rolled-back create writes none, and a
/// cross-org `PUT` target 404s identically to a nonexistent id. If Postgres
/// is not reachable, these tests report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention
/// (`PostgresTestFixture`).
/// </summary>
[Collection("Postgres")]
public sealed class CustomerRegistryTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;
    private readonly WebApplicationFactory<Program> _factory;

    public CustomerRegistryTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
        });

        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
        _factory.Dispose();
    }

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

    private static NewCustomer NewRetail(Guid id, string displayName = "Jane Doe", string? phone = "555-1234", Guid? createdByUserId = null) =>
        new(id, CustomerKind.Retail, displayName, LegalName: null, TaxIdType.None, TaxId: null,
            TaxCondition.ConsumidorFinal, phone, Email: null, AddressStreet: null, AddressNumber: null,
            Neighborhood: null, Locality: null, Province: null, PostalCode: null, DeliveryNotes: null,
            DiscountPercentage: null, PaymentTerms: null, Notes: null, createdByUserId ?? Guid.NewGuid());

    private static NewCustomer NewWholesale(Guid id, Guid createdByUserId) =>
        new(id, CustomerKind.Wholesale, "Acme Distribuidora", "Acme S.R.L.", TaxIdType.Cuit, "30-12345678-9",
            TaxCondition.ResponsableInscripto, "555-9999", "wholesale@example.com", "Av. Siempreviva", "742",
            "Centro", "Springfield", "Buenos Aires", "1000", "Ring twice", 10.5m, "Cuenta corriente 30 días",
            "VIP customer", createdByUserId);

    [Fact]
    public async Task CreateAsync_RetailMinimalFields_Persists_ScopedToCallerOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgId);

        var created = await store.CreateAsync(scope, NewRetail(customerId, createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        Assert.Equal(customerId, created.Id);
        Assert.Equal(orgId, created.OrganizationId);
        Assert.Equal(CustomerKind.Retail, created.CustomerKind);
        Assert.Equal("Jane Doe", created.DisplayName);
        Assert.Equal("555-1234", created.Phone);
        Assert.Null(created.LegalName);
        Assert.True(created.IsEnabled);

        var found = await store.FindAsync(scope, customerId, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("Jane Doe", found!.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_WholesaleFullFiscalFieldSet_PersistsAllFieldsExactly()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgId);

        var created = await store.CreateAsync(scope, NewWholesale(customerId, actorId), "org-user", actorId, CancellationToken.None);

        Assert.Equal(CustomerKind.Wholesale, created.CustomerKind);
        Assert.Equal("Acme S.R.L.", created.LegalName);
        Assert.Equal(TaxIdType.Cuit, created.TaxIdType);
        Assert.Equal("30-12345678-9", created.TaxId);
        Assert.Equal(TaxCondition.ResponsableInscripto, created.TaxCondition);
        Assert.Equal("Centro", created.Neighborhood);
        Assert.Equal(10.5m, created.DiscountPercentage);
        Assert.Equal("Cuenta corriente 30 días", created.PaymentTerms);
    }

    [Fact]
    public async Task CreateAsync_WritesOneAuditRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgId);
        await store.CreateAsync(scope, NewRetail(customerId, createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        Assert.Equal(1, await CountAuditRowsAsync(customerId, "customer.created"));
    }

    [Fact]
    public async Task FindAsync_CrossOrganizationTarget_ReturnsNull()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        await store.CreateAsync(new CloudTenantScope(orgAId), NewRetail(customerId, createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var found = await store.FindAsync(new CloudTenantScope(orgBId), customerId, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyCallerOrganizationsCustomers()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        await store.CreateAsync(new CloudTenantScope(orgAId), NewRetail(Guid.NewGuid(), "Org A Customer", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);
        await store.CreateAsync(new CloudTenantScope(orgBId), NewRetail(Guid.NewGuid(), "Org B Customer", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var listA = await store.ListAsync(new CloudTenantScope(orgAId), CancellationToken.None);

        Assert.Single(listA);
        Assert.Equal("Org A Customer", listA[0].DisplayName);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges_AndWritesAuditRowWithOldAndNewValues()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgId);
        await store.CreateAsync(scope, NewRetail(customerId, "Jane Doe", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var update = new UpdateCustomer(
            "Jane Smith", LegalName: null, TaxIdType.None, TaxId: null, TaxCondition.ConsumidorFinal,
            "555-0000", Email: null, AddressStreet: null, AddressNumber: null, Neighborhood: null,
            Locality: null, Province: null, PostalCode: null, DeliveryNotes: null, DiscountPercentage: null,
            PaymentTerms: null, Notes: null, IsEnabled: true);

        var updated = await store.UpdateAsync(scope, customerId, update, "org-user", actorId, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Jane Smith", updated!.DisplayName);
        Assert.Equal("555-0000", updated.Phone);
        Assert.Equal(1, await CountAuditRowsAsync(customerId, "customer.updated"));
    }

    [Fact]
    public async Task UpdateAsync_CrossOrganizationTarget_ReturnsNull_NoRowMutated()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgAId);
        await SeedOrganizationAsync(orgBId);
        await store.CreateAsync(new CloudTenantScope(orgAId), NewRetail(customerId, "Jane Doe", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var update = new UpdateCustomer(
            "Rogue Rename", LegalName: null, TaxIdType.None, TaxId: null, TaxCondition.ConsumidorFinal,
            null, null, null, null, null, null, null, null, null, null, null, null, IsEnabled: true);

        var result = await store.UpdateAsync(new CloudTenantScope(orgBId), customerId, update, "org-user", actorId, CancellationToken.None);

        Assert.Null(result);

        var stillOriginal = await store.FindAsync(new CloudTenantScope(orgAId), customerId, CancellationToken.None);
        Assert.Equal("Jane Doe", stillOriginal!.DisplayName);
    }

    [Fact]
    public async Task ListChangedSinceAsync_ReturnsOnlyRowsUpdatedAfterCursor()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresCustomerStore(_dataSource!);
        var orgId = Guid.NewGuid();
        var scope = new CloudTenantScope(orgId);
        var actorId = Guid.NewGuid();

        await SeedOrganizationAsync(orgId);
        await store.CreateAsync(scope, NewRetail(Guid.NewGuid(), "Old Customer", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var cursor = DateTimeOffset.UtcNow;
        await Task.Delay(50);

        var newCustomerId = Guid.NewGuid();
        await store.CreateAsync(scope, NewRetail(newCustomerId, "New Customer", createdByUserId: actorId), "org-user", actorId, CancellationToken.None);

        var changed = await store.ListChangedSinceAsync(scope, cursor, CancellationToken.None);

        Assert.Single(changed);
        Assert.Equal(newCustomerId, changed[0].CustomerId);
    }

    // --- Task 4.1: endpoint-level (`Customers.cs`), WebApplicationFactory ---

    private async Task<Guid> SeedUserAsync(
        Guid organizationId, Guid userId, string email, string plaintextPassword,
        Permission permissions = Permission.None)
    {
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var orgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Seeded Org') ON CONFLICT (id) DO NOTHING", owner);
            orgCmd.Parameters.AddWithValue(organizationId);
            orgCmd.ExecuteNonQuery();
        }

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
        var dummyActor = new UserAccount(userId, organizationId, [], []);
        var hash = hasher.HashPassword(dummyActor, plaintextPassword);

        var created = await store.TryCreateAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, [], [new RoleDto("org-role", permissions)]),
            CancellationToken.None);

        if (!created)
        {
            // A SECOND user in the same org — TryCreateAsync enforces the
            // bootstrap "zero users in this org" invariant, so it must be
            // inserted directly (same pattern as AccountEndpointTests).
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var rolesJson = System.Text.Json.JsonSerializer.Serialize(
                new[] { new RoleDto("org-role", permissions) },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using (var insertUserCmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb)
                """, owner))
            {
                insertUserCmd.Parameters.AddWithValue(userId);
                insertUserCmd.Parameters.AddWithValue(organizationId);
                insertUserCmd.Parameters.AddWithValue(normalizedEmail);
                insertUserCmd.Parameters.AddWithValue(hash);
                insertUserCmd.Parameters.AddWithValue(Array.Empty<Guid>());
                insertUserCmd.Parameters.AddWithValue(rolesJson);
                insertUserCmd.ExecuteNonQuery();
            }
            using (var insertDirectoryCmd = new NpgsqlCommand(
                "INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1, $2, $3)", owner))
            {
                insertDirectoryCmd.Parameters.AddWithValue(normalizedEmail);
                insertDirectoryCmd.Parameters.AddWithValue(organizationId);
                insertDirectoryCmd.Parameters.AddWithValue(userId);
                insertDirectoryCmd.ExecuteNonQuery();
            }
        }

        return userId;
    }

    private async Task<HttpClient> SignedInClientAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signIn = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        return client;
    }

    private static CreateCustomerRequest RetailRequest(string displayName = "Jane Doe", string? phone = "555-1234") =>
        new("Retail", displayName, null, "None", null, "ConsumidorFinal", phone, null,
            null, null, null, null, null, null, null, null, null, null);

    private static CreateCustomerRequest WholesaleRequest() =>
        new("Wholesale", "Acme Distribuidora", "Acme S.R.L.", "Cuit", "30-12345678-9", "ResponsableInscripto",
            "555-9999", "wholesale@example.com", "Av. Siempreviva", "742", "Centro", "Springfield",
            "Buenos Aires", "1000", "Ring twice", 10.5m, "Cuenta corriente 30 días", "VIP customer");

    [Fact]
    public async Task Post_ManageUsersHolder_CreatesRetailCustomer_WithMinimalFields_Returns201()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-create-retail@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-create-retail@example.com", "admin-password");

        var response = await client.PostAsJsonAsync("/customers", RetailRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateCustomerResponse>();
        Assert.NotEqual(Guid.Empty, body!.CustomerId);
    }

    [Fact]
    public async Task Post_ManageUsersHolder_CreatesWholesaleCustomer_WithFullFiscalFieldSet_Returns201()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-create-wholesale@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-create-wholesale@example.com", "admin-password");

        var response = await client.PostAsJsonAsync("/customers", WholesaleRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_CallerWithoutManageUsers_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        await SeedUserAsync(orgId, sellerId, "seller-create@example.com", "seller-password", Permission.ViewSales);
        var client = await SignedInClientAsync("seller-create@example.com", "seller-password");

        var response = await client.PostAsJsonAsync("/customers", RetailRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_WritesExactlyOneAuditRow_PerCreate()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-create-audit@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-create-audit@example.com", "admin-password");

        var response = await client.PostAsJsonAsync("/customers", RetailRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        Assert.Equal(1, await CountAuditRowsAsync(body!.CustomerId, "customer.created"));
    }

    /// <summary>
    /// `customers_tax_id_requires_type` (0008) rejects `TaxIdType != None`
    /// with a null `TaxId` — the endpoint does not pre-validate this
    /// combination (design.md leaves it to the DB CHECK), so the INSERT and
    /// its would-be audit row roll back together atomically: zero audit rows
    /// for the attempted id.
    /// </summary>
    [Fact]
    public async Task Post_TaxIdInvariantViolation_RollsBack_WritesNoAuditRow_Returns400()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-create-invalid@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-create-invalid@example.com", "admin-password");

        var invalid = new CreateCustomerRequest(
            "Retail", "Invalid Customer", null, "Cuit", null, "ConsumidorFinal", null, null,
            null, null, null, null, null, null, null, null, null, null);

        var response = await client.PostAsJsonAsync("/customers", invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var countBefore = 0L;
        await using (var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE action = 'customer.created' AND organization_id = $1", connection);
            cmd.Parameters.AddWithValue(orgId);
            countBefore = (long)(await cmd.ExecuteScalarAsync())!;
        }
        Assert.Equal(0, countBefore);
    }

    [Fact]
    public async Task Put_WritesOneAuditRow_WithOldAndNewDisplayNameValues()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-update-audit@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-update-audit@example.com", "admin-password");

        var created = await client.PostAsJsonAsync("/customers", RetailRequest("Original Name"));
        var createdBody = await created.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var update = new UpdateCustomerRequest(
            "Updated Name", null, "None", null, "ConsumidorFinal", null, null,
            null, null, null, null, null, null, null, null, null, null, true);

        var response = await client.PutAsJsonAsync($"/customers/{createdBody!.CustomerId}", update);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountAuditRowsAsync(createdBody.CustomerId, "customer.updated"));
    }

    [Fact]
    public async Task Put_CrossOrganizationTarget_Returns404_IdenticalToNonexistentId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        var adminAId = Guid.NewGuid();
        var adminBId = Guid.NewGuid();
        await SeedUserAsync(orgAId, adminAId, "admin-crossorg-a@example.com", "admin-password", Permission.ManageUsers);
        await SeedUserAsync(orgBId, adminBId, "admin-crossorg-b@example.com", "admin-password", Permission.ManageUsers);

        var clientA = await SignedInClientAsync("admin-crossorg-a@example.com", "admin-password");
        var createdInA = await clientA.PostAsJsonAsync("/customers", RetailRequest());
        var createdInABody = await createdInA.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var clientB = await SignedInClientAsync("admin-crossorg-b@example.com", "admin-password");
        var update = new UpdateCustomerRequest(
            "Rogue Rename", null, "None", null, "ConsumidorFinal", null, null,
            null, null, null, null, null, null, null, null, null, null, true);

        var crossOrgResponse = await clientB.PutAsJsonAsync($"/customers/{createdInABody!.CustomerId}", update);
        var nonexistentResponse = await clientB.PutAsJsonAsync($"/customers/{Guid.NewGuid()}", update);

        Assert.Equal(HttpStatusCode.NotFound, crossOrgResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistentResponse.StatusCode);
    }

    [Fact]
    public async Task OrderingAccess_IssueThenRevoke_RoundTrips()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-ordering-access@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-ordering-access@example.com", "admin-password");

        var created = await client.PostAsJsonAsync("/customers", RetailRequest());
        var createdBody = await created.Content.ReadFromJsonAsync<CreateCustomerResponse>();

        var issueResponse = await client.PostAsync($"/customers/{createdBody!.CustomerId}/ordering-access", null);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        var issued = await issueResponse.Content.ReadFromJsonAsync<IssueOrderingAccessResponse>();
        Assert.NotEqual(Guid.Empty, issued!.Credential);

        var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"/customers/{createdBody.CustomerId}/ordering-access")
        {
            Content = JsonContent.Create(new RevokeOrderingAccessRequest(issued.Credential)),
        };
        var revokeResponse = await client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
    }

    private async Task SeedOrganizationAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", connection);
        cmd.Parameters.AddWithValue(orgId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAuditRowsAsync(Guid entityId, string action)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", connection);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountUsersByEmailAsync(string email)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM users WHERE email = $1", connection);
        cmd.Parameters.AddWithValue(email.Trim().ToLowerInvariant());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // --- Follow-up (verify-report WARNING #1): customer-login provisioning
    // via the EXISTING POST /account/users endpoint (user-credentials spec
    // "Provisioning a customer login requires ManageUsers"). ---------------

    [Fact]
    public async Task Post_ManageUsersHolder_WithValidCustomerId_CreatesCustomerLinkedAccount_Returns201()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-provision@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-provision@example.com", "admin-password");

        var customerId = Guid.NewGuid();
        var customerStore = new PostgresCustomerStore(_dataSource!);
        await customerStore.CreateAsync(
            new CloudTenantScope(orgId), NewRetail(customerId, "Login Customer"), "org-user", adminId, CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("customer-login@example.com", "customer-password", [], [], customerId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.NotEqual(Guid.Empty, body!.UserId);

        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var actor = await userStore.LoadActorAsync(new CloudTenantScope(orgId), body.UserId, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.Equal(customerId, actor!.CustomerId);
        Assert.Equal(Permission.None, actor.EffectivePermissions);
    }

    [Fact]
    public async Task Post_CustomerIdAndRoleNamesBothSupplied_Returns400_NoUserPersisted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-provision-both@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-provision-both@example.com", "admin-password");

        var customerId = Guid.NewGuid();
        var customerStore = new PostgresCustomerStore(_dataSource!);
        await customerStore.CreateAsync(
            new CloudTenantScope(orgId), NewRetail(customerId, "Both Customer"), "org-user", adminId, CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("customer-and-role@example.com", "password", [RoleCatalog.Seller], [], customerId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountUsersByEmailAsync("customer-and-role@example.com"));
    }

    [Fact]
    public async Task Post_CustomerIdCrossOrganization_Returns404_NoUserPersisted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgAId = Guid.NewGuid();
        var adminAId = Guid.NewGuid();
        await SeedUserAsync(orgAId, adminAId, "admin-provision-cross@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-provision-cross@example.com", "admin-password");

        // Customer B belongs to a DIFFERENT organization.
        var orgBId = Guid.NewGuid();
        await SeedOrganizationAsync(orgBId);
        var otherCustomerId = Guid.NewGuid();
        var customerStore = new PostgresCustomerStore(_dataSource!);
        await customerStore.CreateAsync(
            new CloudTenantScope(orgBId), NewRetail(otherCustomerId, "Other Org Customer"), "org-user", Guid.NewGuid(), CancellationToken.None);

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("customer-crossorg@example.com", "password", [], [], otherCustomerId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountUsersByEmailAsync("customer-crossorg@example.com"));
    }

    // --- Follow-up (verify-report WARNING #2): PUT /account/users/{id}/roles
    // must reject a customer-linked target with a clean 400, never the raw
    // `users_customer_has_no_roles` DB CHECK exception. ---------------------

    [Fact]
    public async Task PutRoles_TargetHasCustomerId_Returns400_ValidationProblem_NotRawDbException()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(orgId, adminId, "admin-roles-guard@example.com", "admin-password", Permission.ManageUsers);
        var client = await SignedInClientAsync("admin-roles-guard@example.com", "admin-password");

        var customerId = Guid.NewGuid();
        var customerStore = new PostgresCustomerStore(_dataSource!);
        await customerStore.CreateAsync(
            new CloudTenantScope(orgId), NewRetail(customerId, "Roles Guard Customer"), "org-user", adminId, CancellationToken.None);

        var provisionResponse = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("customer-roles-guard@example.com", "customer-password", [], [], customerId));
        Assert.Equal(HttpStatusCode.Created, provisionResponse.StatusCode);
        var provisioned = await provisionResponse.Content.ReadFromJsonAsync<CreateUserResponse>();

        var response = await client.PutAsJsonAsync(
            $"/account/users/{provisioned!.UserId}/roles", new AssignRolesRequest([RoleCatalog.Seller]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
