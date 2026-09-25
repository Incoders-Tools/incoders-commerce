using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

[Collection("Postgres")]
public sealed class AdminConsoleTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public AdminConsoleTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString).UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndReset();
    }

    public void Dispose() => _factory.Dispose();

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Commerce.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "deploy", "db", "migrations"), "*.sql").OrderBy(Path.GetFileName))
        {
            var sql = File.ReadAllText(file)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid UserId)> BootstrapAsync(string email, string password)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.UserId);
    }

    private string Hash(Guid userId, Guid organizationId, string password)
    {
        var hasher = _factory.Services.GetRequiredService<PasswordHasher<UserAccount>>();
        return hasher.HashPassword(new UserAccount(userId, organizationId, [], []), password);
    }

    private static void SeedUser(Guid organizationId, Guid userId, string email, string hash, Permission permissions, Guid? customerId = null)
    {
        var roles = customerId is null ? new[] { new RoleDto("staff", permissions) } : Array.Empty<RoleDto>();
        var rolesJson = JsonSerializer.Serialize(roles, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using (var user = new NpgsqlCommand("INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles, customer_id) VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7)", owner))
        {
            user.Parameters.AddWithValue(userId); user.Parameters.AddWithValue(organizationId); user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(hash); user.Parameters.AddWithValue(Array.Empty<Guid>()); user.Parameters.AddWithValue(rolesJson);
            user.Parameters.AddWithValue(customerId is null ? DBNull.Value : customerId.Value); user.ExecuteNonQuery();
        }
        using var directory = new NpgsqlCommand("INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1,$2,$3)", owner);
        directory.Parameters.AddWithValue(email); directory.Parameters.AddWithValue(organizationId); directory.Parameters.AddWithValue(userId); directory.ExecuteNonQuery();
    }

    private static void SeedCustomer(Guid organizationId, Guid customerId, Guid creatorId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString); owner.Open();
        using var customer = new NpgsqlCommand("INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id) VALUES ($1,$2,'Retail','Linked customer',$3)", owner);
        customer.Parameters.AddWithValue(customerId); customer.Parameters.AddWithValue(organizationId); customer.Parameters.AddWithValue(creatorId); customer.ExecuteNonQuery();
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task ListStaff_ReturnsOnlySameOrganizationNonCustomerStaff()
    {
        if (!_postgresAvailable) return;
        const string adminEmail = "staff-admin@example.com"; const string password = "correct-password";
        var (organizationId, adminId) = await BootstrapAsync(adminEmail, password);
        var staffId = Guid.NewGuid(); SeedUser(organizationId, staffId, "staff@example.com", Hash(staffId, organizationId, password), Permission.ViewSales);
        var customerId = Guid.NewGuid(); SeedCustomer(organizationId, customerId, adminId);
        var customerUserId = Guid.NewGuid(); SeedUser(organizationId, customerUserId, "customer@example.com", Hash(customerUserId, organizationId, password), Permission.None, customerId);
        var otherOrg = Guid.NewGuid(); var otherUser = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var org = new NpgsqlCommand("INSERT INTO organizations (id,name) VALUES ($1,'Other')", owner); org.Parameters.AddWithValue(otherOrg); org.ExecuteNonQuery(); }
        SeedUser(otherOrg, otherUser, "other@example.com", Hash(otherUser, otherOrg, password), Permission.ManageUsers);

        var response = await (await SignInAsync(adminEmail, password)).GetAsync("/account/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<List<UserSummaryDto>>();
        Assert.NotNull(users);
        Assert.Contains(users!, user => user.UserId == adminId);
        Assert.Contains(users!, user => user.UserId == staffId);
        Assert.DoesNotContain(users!, user => user.UserId == customerUserId || user.UserId == otherUser);
    }

    [Fact]
    public async Task ListStaff_CallerWithoutManageUsers_Returns403()
    {
        if (!_postgresAvailable) return;
        const string adminEmail = "denial-admin@example.com"; const string password = "correct-password";
        var (organizationId, _) = await BootstrapAsync(adminEmail, password);
        var restrictedId = Guid.NewGuid();
        SeedUser(organizationId, restrictedId, "restricted@example.com", Hash(restrictedId, organizationId, password), Permission.ViewSales);

        var response = await (await SignInAsync("restricted@example.com", password)).GetAsync("/account/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task Branches_ManageBranchSettingsCallerCreatesAndListsOnlyOwnOrganization()
    {
        if (!_postgresAvailable) return;
        const string email = "branch-admin@example.com"; const string password = "correct-password";
        var (organizationId, _) = await BootstrapAsync(email, password);
        var client = await SignInAsync(email, password);

        var create = await client.PostAsJsonAsync("/account/branches", new CreateBranchRequest("Downtown"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateBranchResponse>();

        var otherOrganizationId = Guid.NewGuid(); var otherBranchId = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using (var org = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Other')", owner)) { org.Parameters.AddWithValue(otherOrganizationId); org.ExecuteNonQuery(); }
            using var branch = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Hidden')", owner);
            branch.Parameters.AddWithValue(otherBranchId); branch.Parameters.AddWithValue(otherOrganizationId); branch.ExecuteNonQuery();
        }

        var list = await client.GetAsync("/account/branches");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var branches = await list.Content.ReadFromJsonAsync<List<BranchSummaryDto>>();
        Assert.Contains(branches!, branch => branch.BranchId == created!.BranchId && branch.BranchName == "Downtown");
        Assert.DoesNotContain(branches!, branch => branch.BranchId == otherBranchId);
    }

    [Fact]
    public async Task Branches_CallerWithoutManageBranchSettingsReturns403AndDoesNotWrite()
    {
        if (!_postgresAvailable) return;
        const string email = "branch-denial-admin@example.com"; const string password = "correct-password";
        var (organizationId, _) = await BootstrapAsync(email, password);
        var restrictedId = Guid.NewGuid();
        SeedUser(organizationId, restrictedId, "branch-restricted@example.com", Hash(restrictedId, organizationId, password), Permission.ViewSales);
        var client = await SignInAsync("branch-restricted@example.com", password);

        var response = await client.PostAsJsonAsync("/account/branches", new CreateBranchRequest("Denied"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString); owner.Open();
        using var count = new NpgsqlCommand("SELECT count(*) FROM branches WHERE organization_id = $1 AND name = 'Denied'", owner);
        count.Parameters.AddWithValue(organizationId);
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }    [Fact]
    public async Task Organizations_SystemAdminListsAcrossOrganizations_AndNonSystemAdminIsDenied()
    {
        if (!_postgresAvailable) return;
        const string email = "organizations-sysadmin@example.com"; const string password = "correct-password";
        var (organizationId, userId) = await BootstrapAsync(email, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(userId); flag.ExecuteNonQuery(); }
        var sysadmin = await SignInAsync(email, password);
        var otherId = Guid.NewGuid(); using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var org = new NpgsqlCommand("INSERT INTO organizations (id,name) VALUES ($1,'Other organization')", owner); org.Parameters.AddWithValue(otherId); org.ExecuteNonQuery(); }
        var response = await sysadmin.GetAsync("/account/organizations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var organizations = await response.Content.ReadFromJsonAsync<List<OrganizationSummary>>();
        Assert.Contains(organizations!, org => org.Id == organizationId);
        Assert.Contains(organizations!, org => org.Id == otherId);

        var ordinary = await SignInAsync("organizations-sysadmin@example.com", password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = false WHERE id = $1", owner); flag.Parameters.AddWithValue(userId); flag.ExecuteNonQuery(); }
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/account/organizations")).StatusCode);
    }

    [Fact]
    public async Task Organizations_SystemAdminBootstrapsOrganizationWhoseAdminCanSignIn()
    {
        if (!_postgresAvailable) return;
        const string email = "bootstrap-sysadmin@example.com"; const string password = "correct-password";
        var (_, userId) = await BootstrapAsync(email, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(userId); flag.ExecuteNonQuery(); }
        var client = await SignInAsync(email, password);
        var response = await client.PostAsJsonAsync("/account/organizations", new CreateOrganizationRequest("New org", "Main", "new-admin@example.com", password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>();
Assert.Equal(created!.OrganizationId, (await (await SignInAsync("new-admin@example.com", password)).GetFromJsonAsync<SignedInResponse>("/account/me"))!.OrganizationId);
        using var auditOwner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        auditOwner.Open();
        using var audit = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE actor_id = $1 AND organization_id = $2 AND action = 'organization.bootstrapped'", auditOwner);
        audit.Parameters.AddWithValue(userId);
        audit.Parameters.AddWithValue(created.OrganizationId);
        Assert.Equal(1L, (long)audit.ExecuteScalar()!);
    }

    [Fact]
    public async Task Organizations_SystemAdminListFailsClosed_WhenPlatformReadDatasourceIsMissing()
    {
        if (!_postgresAvailable) return;
        const string email = "organizations-no-read@example.com"; const string password = "correct-password";
        var (_, userId) = await BootstrapAsync(email, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner);
            flag.Parameters.AddWithValue(userId);
            flag.ExecuteNonQuery();
        }

        using var noReadFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:CommercePlatformRead", ""));
        using var client = noReadFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/account/organizations")).StatusCode);
    }

    // --- T5a: organization branding (organization-persistence spec
    // "Organization Branding Fields") ---------------------------------------

    [Fact]
    public async Task Organizations_Branding_SystemAdminSetsReadsAndClears()
    {
        if (!_postgresAvailable) return;
        const string email = "branding-sysadmin@example.com"; const string password = "correct-password";
        var (organizationId, userId) = await BootstrapAsync(email, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(userId); flag.ExecuteNonQuery(); }
        var sysadmin = await SignInAsync(email, password);

        var initial = await (await sysadmin.GetAsync($"/account/organizations/{organizationId}/branding")).Content.ReadFromJsonAsync<OrganizationBrandingResponse>();
        Assert.Null(initial!.LogoUrl);
        Assert.Null(initial.PrimaryColor);

        var setResponse = await sysadmin.PutAsJsonAsync(
            $"/account/organizations/{organizationId}/branding",
            new UpdateOrganizationBrandingRequest("https://cdn.example.com/logo.png", "#336699"));
        Assert.Equal(HttpStatusCode.NoContent, setResponse.StatusCode);

        var afterSet = await (await sysadmin.GetAsync($"/account/organizations/{organizationId}/branding")).Content.ReadFromJsonAsync<OrganizationBrandingResponse>();
        Assert.Equal("https://cdn.example.com/logo.png", afterSet!.LogoUrl);
        Assert.Equal("#336699", afterSet.PrimaryColor);

        using (var auditOwner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            auditOwner.Open();
            using var audit = new NpgsqlCommand(
                "SELECT count(*) FROM audit_log WHERE actor_id = $1 AND organization_id = $2 AND action = 'organization.branding_updated'", auditOwner);
            audit.Parameters.AddWithValue(userId);
            audit.Parameters.AddWithValue(organizationId);
            Assert.Equal(1L, (long)audit.ExecuteScalar()!);
        }

        var clearResponse = await sysadmin.PutAsJsonAsync(
            $"/account/organizations/{organizationId}/branding",
            new UpdateOrganizationBrandingRequest("", ""));
        Assert.Equal(HttpStatusCode.NoContent, clearResponse.StatusCode);

        var afterClear = await (await sysadmin.GetAsync($"/account/organizations/{organizationId}/branding")).Content.ReadFromJsonAsync<OrganizationBrandingResponse>();
        Assert.Null(afterClear!.LogoUrl);
        Assert.Null(afterClear.PrimaryColor);
    }

    [Fact]
    public async Task Organizations_Branding_NonSystemAdminIsDeniedAndUnknownOrganizationIs404()
    {
        if (!_postgresAvailable) return;
        const string sysadminEmail = "branding-gate-sysadmin@example.com";
        const string ordinaryEmail = "branding-gate-ordinary@example.com";
        const string password = "correct-password";
        var (organizationId, sysadminUserId) = await BootstrapAsync(sysadminEmail, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(sysadminUserId); flag.ExecuteNonQuery(); }
        var sysadmin = await SignInAsync(sysadminEmail, password);

        var ordinaryId = Guid.NewGuid();
        SeedUser(organizationId, ordinaryId, ordinaryEmail, Hash(ordinaryId, organizationId, password), Permission.ManageUsers);
        var ordinary = await SignInAsync(ordinaryEmail, password);

        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync($"/account/organizations/{organizationId}/branding")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await ordinary.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest("https://example.com/logo.png", "#abcdef"))).StatusCode);

        var unknownId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync($"/account/organizations/{unknownId}/branding")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{unknownId}/branding", new UpdateOrganizationBrandingRequest("https://example.com/logo.png", "#abcdef"))).StatusCode);
    }

    [Fact]
    public async Task Organizations_Branding_ValidationRejectsBadUrlAndColor_AndPersistsNothing()
    {
        if (!_postgresAvailable) return;
        const string email = "branding-validation@example.com"; const string password = "correct-password";
        var (organizationId, userId) = await BootstrapAsync(email, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(userId); flag.ExecuteNonQuery(); }
        var sysadmin = await SignInAsync(email, password);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest("ftp://example.com/logo.png", null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest("/logo.png", null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest("https://example.com/" + new string('a', 2100), null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest(null, "#fff"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationId}/branding", new UpdateOrganizationBrandingRequest(null, "336699"))).StatusCode);

        var stillUnset = await (await sysadmin.GetAsync($"/account/organizations/{organizationId}/branding")).Content.ReadFromJsonAsync<OrganizationBrandingResponse>();
        Assert.Null(stillUnset!.LogoUrl);
        Assert.Null(stillUnset.PrimaryColor);
    }

    [Fact]
    public async Task Organizations_Branding_OwnOrganizationEndpointReturnsOnlyTheCallersOrg()
    {
        if (!_postgresAvailable) return;
        const string sysadminEmail = "branding-own-sysadmin@example.com"; const string password = "correct-password";
        var (organizationAId, sysadminUserId) = await BootstrapAsync(sysadminEmail, password);
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString)) { owner.Open(); using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true WHERE id = $1", owner); flag.Parameters.AddWithValue(sysadminUserId); flag.ExecuteNonQuery(); }
        var sysadmin = await SignInAsync(sysadminEmail, password);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationAId}/branding", new UpdateOrganizationBrandingRequest("https://a.example.com/logo.png", "#111111"))).StatusCode);

        var (organizationBId, _) = await BootstrapAsync("branding-own-org-b-admin@example.com", password);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await sysadmin.PutAsJsonAsync($"/account/organizations/{organizationBId}/branding", new UpdateOrganizationBrandingRequest("https://b.example.com/logo.png", "#222222"))).StatusCode);

        var staffId = Guid.NewGuid();
        SeedUser(organizationBId, staffId, "branding-own-org-b-staff@example.com", Hash(staffId, organizationBId, password), Permission.ViewSales);
        var staffInB = await SignInAsync("branding-own-org-b-staff@example.com", password);

        var response = await staffInB.GetAsync("/account/organization/branding");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OrganizationBrandingResponse>();
        Assert.Equal("https://b.example.com/logo.png", body!.LogoUrl);
        Assert.Equal("#222222", body.PrimaryColor);
        Assert.NotEqual("https://a.example.com/logo.png", body.LogoUrl);
    }

    [Fact]
    public void AdminConsoleMigration_EmailCollision_PreservesLegacyPlatformCredential()
    {
        if (!_postgresAvailable) return;

        const string email = "migration-collision@example.com";
        var organizationId = Guid.NewGuid();
        var existingUserId = Guid.NewGuid();
        var legacyAdminId = Guid.NewGuid();
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        try
        {
            using (var setup = new NpgsqlBatch(owner))
            {
                setup.BatchCommands.Add(new NpgsqlBatchCommand("CREATE TABLE platform_admins (id uuid PRIMARY KEY, email text NOT NULL UNIQUE, password_hash text NOT NULL, created_at_utc timestamptz NOT NULL DEFAULT now(), last_sign_in_at_utc timestamptz NULL)"));
                var organization = new NpgsqlBatchCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Collision Org')"); organization.Parameters.AddWithValue(organizationId); setup.BatchCommands.Add(organization);
                var user = new NpgsqlBatchCommand("INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1, $2, $3, 'existing-hash', '{}', '[]')"); user.Parameters.AddWithValue(existingUserId); user.Parameters.AddWithValue(organizationId); user.Parameters.AddWithValue(email); setup.BatchCommands.Add(user);
                var userDirectory = new NpgsqlBatchCommand("INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1, $2, $3)"); userDirectory.Parameters.AddWithValue(email); userDirectory.Parameters.AddWithValue(organizationId); userDirectory.Parameters.AddWithValue(existingUserId); setup.BatchCommands.Add(userDirectory);
                var legacy = new NpgsqlBatchCommand("INSERT INTO platform_admins (id, email, password_hash) VALUES ($1, $2, 'legacy-hash')"); legacy.Parameters.AddWithValue(legacyAdminId); legacy.Parameters.AddWithValue(email); setup.BatchCommands.Add(legacy);
                setup.ExecuteNonQuery();
            }

            var migration = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0012_admin_console.sql"));
            Assert.Throws<PostgresException>(() => new NpgsqlCommand(migration, owner).ExecuteNonQuery());

            using var preserved = new NpgsqlCommand("SELECT password_hash FROM platform_admins WHERE id = $1", owner);
            preserved.Parameters.AddWithValue(legacyAdminId);
            Assert.Equal("legacy-hash", (string?)preserved.ExecuteScalar());
            using var directory = new NpgsqlCommand("SELECT user_id FROM user_directory WHERE email_normalized = $1", owner);
            directory.Parameters.AddWithValue(email);
            Assert.Equal(existingUserId, (Guid?)directory.ExecuteScalar());
        }
        finally
        {
            using (var drop = new NpgsqlCommand("DROP TABLE IF EXISTS platform_admins", owner)) drop.ExecuteNonQuery();
            using (var directory = new NpgsqlCommand("DELETE FROM user_directory WHERE email_normalized = $1", owner)) { directory.Parameters.AddWithValue(email); directory.ExecuteNonQuery(); }
            using (var user = new NpgsqlCommand("DELETE FROM users WHERE email = $1", owner)) { user.Parameters.AddWithValue(email); user.ExecuteNonQuery(); }
            using var organization = new NpgsqlCommand("DELETE FROM organizations WHERE id = $1", owner);
            organization.Parameters.AddWithValue(organizationId); organization.ExecuteNonQuery();
        }
    }
}
