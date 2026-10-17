using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// railway-deploy-pipeline S16: the server rejects a NEW staff or system-admin password shorter than 12 characters on
/// every write path (reset confirm, renewal, user creation, admin reset, organization bootstrap admin, the first-admin
/// bootstrap) with that endpoint's 400 ValidationProblem, and the rejected request changes nothing. Sign-in is not a
/// write: an existing shorter password still signs in. Customer-linked logins are customer accounts, outside the rule.
/// </summary>
[Collection("Postgres")]
public sealed class PasswordMinimumLengthTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Eleven = "eleven-char";
    private const string Twelve = "twelve-chars";
    private const string Existing = "existing-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PasswordMinimumLengthTests(WebApplicationFactory<Program> factory)
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
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private static T Scalar<T>(string sql, params object[] parameters)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void Execute(string sql, params object[] parameters)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter);
        cmd.ExecuteNonQuery();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<HttpStatusCode> SignInStatusAsync(string email, string password) =>
        (await NewClient().PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password))).StatusCode;

    /// <summary>The first admin of a new organization (business-admin: ManageUsers), through the real bootstrap.</summary>
    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email, string password)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await NewClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.BranchId, body.UserId);
    }

    /// <summary>A staff user with a password shorter than the rule, as stored before the rule existed.</summary>
    private void SeedStaff(Guid organizationId, Guid userId, string email, string password, Guid[] branchIds)
    {
        var hash = _factory.Services.GetRequiredService<PasswordHasher<UserAccount>>()
            .HashPassword(new UserAccount(userId, organizationId, [], []), password);
        var roles = JsonSerializer.Serialize(
            new[] { new RoleDto(RoleCatalog.Cashier, Permission.ViewSales) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Execute(
            "INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1,$2,$3,$4,$5,$6::jsonb)",
            userId, organizationId, email, hash, branchIds, roles);
        Execute("INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1,$2,$3)", email, organizationId, userId);
    }

    private static string PasswordHash(Guid userId) => Scalar<string>("SELECT password_hash FROM users WHERE id = $1", userId);

    private static int SessionVersion(Guid userId) => Scalar<int>("SELECT session_version FROM users WHERE id = $1", userId);

    /// <summary>The endpoint's 400 ValidationProblem, naming the field and the rule.</summary>
    private static async Task AssertTooShortAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = body.GetProperty("errors");
        Assert.Equal([field], errors.EnumerateObject().Select(property => property.Name));
        Assert.Equal($"{field} must be at least 12 characters.", errors.GetProperty(field)[0].GetString());
    }

    // ---- reset confirm ---------------------------------------------------------------------------

    [Fact]
    public async Task ResetConfirm_ElevenCharacters_Returns400_AndLeavesTheTokenUnconsumed_TwelveIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var email = Unique("reset-confirm");
        var (organizationId, _, userId) = await BootstrapAsync(email, Existing);
        var token = "reset-token-" + Guid.NewGuid().ToString("N");
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await _factory.Services.GetRequiredService<PostgresPasswordRecoveryStore>().IssueTokenAsync(
            new CloudTenantScope(organizationId), userId, tokenHash, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var hashBefore = PasswordHash(userId);
        var versionBefore = SessionVersion(userId);

        var rejected = await NewClient().PostAsJsonAsync("/account/reset-password/confirm", new ConfirmResetPasswordRequest(token, Eleven));

        await AssertTooShortAsync(rejected, "newPassword");
        Assert.True(Scalar<bool>("SELECT consumed_at IS NULL FROM password_reset_tokens WHERE token_hash = $1", tokenHash));
        Assert.Equal(hashBefore, PasswordHash(userId));
        Assert.Equal(versionBefore, SessionVersion(userId));

        var accepted = await NewClient().PostAsJsonAsync("/account/reset-password/confirm", new ConfirmResetPasswordRequest(token, Twelve));

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(email, Twelve));
    }

    // ---- renewal -------------------------------------------------------------------------------------

    [Fact]
    public async Task Renew_ElevenCharacters_Returns400_AndKeepsTheCurrentPassword_TwelveIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var email = Unique("renew");
        var (_, _, userId) = await BootstrapAsync(email, Existing);
        var client = await SignInAsync(email, Existing);
        var hashBefore = PasswordHash(userId);
        var versionBefore = SessionVersion(userId);

        var rejected = await client.PostAsJsonAsync("/account/renew-password", new RenewPasswordRequest(Existing, Eleven));

        await AssertTooShortAsync(rejected, "newPassword");
        Assert.Equal(hashBefore, PasswordHash(userId));
        Assert.Equal(versionBefore, SessionVersion(userId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/me")).StatusCode);

        var accepted = await client.PostAsJsonAsync("/account/renew-password", new RenewPasswordRequest(Existing, Twelve));

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(email, Twelve));
    }

    // ---- first admin bootstrap -----------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_ElevenCharacters_Returns400_WritesNothing_AndTheTokenStaysConsumable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var email = Unique("bootstrap");
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);

        var rejected = await NewClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Acme", "HQ", email, Eleven));

        await AssertTooShortAsync(rejected, "password");
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM organizations WHERE id = $1", organizationId));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users WHERE email = $1", email));

        var accepted = await NewClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Acme", "HQ", email, Twelve));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(email, Twelve));
    }

    // ---- organization bootstrap admin (system admin) -------------------------------------------------

    [Fact]
    public async Task CreateOrganization_ElevenCharacterAdminPassword_Returns400_AndCreatesNothing_TwelveIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var sysadminEmail = Unique("sysadmin");
        var (_, _, sysadminId) = await BootstrapAsync(sysadminEmail, Existing);
        Execute("UPDATE users SET is_system_admin = true WHERE id = $1", sysadminId);
        var client = await SignInAsync(sysadminEmail, Existing);
        var adminEmail = Unique("new-org-admin");
        var organizationsBefore = Scalar<long>("SELECT count(*) FROM organizations");

        var rejected = await client.PostAsJsonAsync("/account/organizations", new CreateOrganizationRequest("New org", "Main", adminEmail, Eleven));

        await AssertTooShortAsync(rejected, "adminPassword");
        Assert.Equal(organizationsBefore, Scalar<long>("SELECT count(*) FROM organizations"));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users WHERE email = $1", adminEmail));

        var accepted = await client.PostAsJsonAsync("/account/organizations", new CreateOrganizationRequest("New org", "Main", adminEmail, Twelve));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(adminEmail, Twelve));
    }

    // ---- admin reset of a user's password ------------------------------------------------------------

    [Fact]
    public async Task AdminReset_ElevenCharacters_Returns400_AndKeepsTheTargetsPassword_TwelveIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminEmail = Unique("reset-admin");
        var (organizationId, branchId, _) = await BootstrapAsync(adminEmail, Existing);
        var targetId = Guid.NewGuid();
        var targetEmail = Unique("reset-target");
        SeedStaff(organizationId, targetId, targetEmail, "short", [branchId]);
        var admin = await SignInAsync(adminEmail, Existing);
        var hashBefore = PasswordHash(targetId);
        var versionBefore = SessionVersion(targetId);

        var rejected = await admin.PostAsJsonAsync($"/account/users/{targetId}/reset-password", new AdminResetPasswordRequest(Eleven));

        await AssertTooShortAsync(rejected, "newPassword");
        Assert.Equal(hashBefore, PasswordHash(targetId));
        Assert.Equal(versionBefore, SessionVersion(targetId));

        var accepted = await admin.PostAsJsonAsync($"/account/users/{targetId}/reset-password", new AdminResetPasswordRequest(Twelve));

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(targetEmail, Twelve));
    }

    // ---- user creation -------------------------------------------------------------------------------

    [Fact]
    public async Task CreateStaffUser_ElevenCharacters_Returns400_AndCreatesNoUser_TwelveIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminEmail = Unique("create-admin");
        var (_, branchId, _) = await BootstrapAsync(adminEmail, Existing);
        var admin = await SignInAsync(adminEmail, Existing);
        var staffEmail = Unique("new-staff");

        var rejected = await admin.PostAsJsonAsync("/account/users", new CreateUserRequest(staffEmail, Eleven, [RoleCatalog.Cashier], [branchId]));

        await AssertTooShortAsync(rejected, "password");
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM users WHERE email = $1", staffEmail));
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM user_directory WHERE email_normalized = $1", staffEmail));

        var accepted = await admin.PostAsJsonAsync("/account/users", new CreateUserRequest(staffEmail, Twelve, [RoleCatalog.Cashier], [branchId]));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(staffEmail, Twelve));
    }

    [Fact]
    public async Task CreateCustomerLinkedLogin_IsACustomerAccount_OutsideTheStaffRule()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var adminEmail = Unique("customer-admin");
        var (organizationId, _, adminId) = await BootstrapAsync(adminEmail, Existing);
        var customerId = Guid.NewGuid();
        Execute(
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id) VALUES ($1,$2,'Retail','Linked customer',$3)",
            customerId, organizationId, adminId);
        var admin = await SignInAsync(adminEmail, Existing);

        var response = await admin.PostAsJsonAsync("/account/users", new CreateUserRequest(Unique("customer-login"), "short", [], [], customerId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- sign-in is not a write ----------------------------------------------------------------------

    [Fact]
    public async Task SignIn_WithAnExistingShorterPassword_StillSucceeds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, branchId, _) = await BootstrapAsync(Unique("signin-admin"), Existing);
        var email = Unique("short-signin");
        SeedStaff(organizationId, Guid.NewGuid(), email, "short", [branchId]);

        Assert.Equal(HttpStatusCode.OK, await SignInStatusAsync(email, "short"));
    }
}
