using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Covers commerce-pos-user-login Unit 2 (design.md "Provisioning endpoint",
/// "Staleness TTL and reconciliation trigger"): `POST
/// /device/operators/verify` and `GET /device/operators/{userId}/status`,
/// both `.RequireAuthorization("DeviceBearer")` reusing `/device/pair`'s
/// exact credential-verification path.
/// </summary>
[Collection("Postgres")]
public sealed class OperatorProvisioningTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public OperatorProvisioningTests(WebApplicationFactory<Program> factory)
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

        var initSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var orgsSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(orgsSql, owner)) cmd.ExecuteNonQuery();

        var deviceSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0004_device_credentials.sql"));
        using (var cmd = new NpgsqlCommand(deviceSql, owner)) cmd.ExecuteNonQuery();

        var recoverySql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0005_password_recovery.sql"));
        using (var cmd = new NpgsqlCommand(recoverySql, owner)) cmd.ExecuteNonQuery();

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, sync_inbox, user_directory, users, device_credentials, branches, organizations", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(Guid OrgId, Guid BranchId, Guid UserId)> SeedOperatorAsync(
        string email, string password, bool includeBranchInScope = true)
    {
        using var scope = _factory.Services.CreateScope();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, orgId, [], []), password);

        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId),
            new NewOrganization(orgId, "Operator Provisioning Co"),
            new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, email, passwordHash, includeBranchInScope ? [branchId] : [], [new RoleDto("cashier", Permission.ViewSales)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);

        return (orgId, branchId, userId);
    }

    private async Task RevokeUserAsync(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("UPDATE users SET is_revoked = true WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }

    private async Task<string> IssueDeviceTokenAsync(Guid orgId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return issued.PlaintextToken;
    }

    private async Task<string> IssueRevokedDeviceTokenAsync(Guid orgId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        await credentialStore.RevokeAsync(new CloudTenantScope(orgId), issued.Record.Id, CancellationToken.None);
        return issued.PlaintextToken;
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, string? deviceToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (deviceToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        }
        return request;
    }

    // --- POST /device/operators/verify --------------------------------------

    [Fact]
    public async Task Verify_ValidCredentials_Returns200_WithRealOperatorIdentity()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOperatorAsync("verify-valid@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("verify-valid@example.com", "correct-password"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorVerifyResponse>();
        Assert.Equal("verified", body!.Status);
        Assert.Equal(userId, body.UserId);
        Assert.Equal("verify-valid@example.com", body.Email);
        Assert.Equal(orgId, body.OrganizationId);
    }

    [Fact]
    public async Task Verify_UnknownEmail_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedOperatorAsync("verify-owner@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("nobody-operator@example.com", "whatever"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_WrongPassword_Returns401_IdenticalShapeToUnknownEmail()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedOperatorAsync("verify-wrongpass@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("verify-wrongpass@example.com", "incorrect-password"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var unknownEmailResponse = await client.SendAsync(BuildRequest(
            HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("nobody-at-all@example.com", "whatever")));
        Assert.Equal(await response.Content.ReadAsStringAsync(), await unknownEmailResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Verify_RevokedUser_Returns401_SameShape()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOperatorAsync("verify-revoked@example.com", "correct-password");
        await RevokeUserAsync(userId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("verify-revoked@example.com", "correct-password"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_BranchNotInScope_Returns403_BranchNotInScope()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedOperatorAsync(
            "verify-outofscope@example.com", "correct-password", includeBranchInScope: false);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("verify-outofscope@example.com", "correct-password"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorVerifyResponse>();
        Assert.Equal("branch-not-in-scope", body!.Status);
    }

    [Fact]
    public async Task Verify_NoResponseEverCarriesSetCookie()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedOperatorAsync("verify-nocookie@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken,
            new OperatorVerifyRequest("verify-nocookie@example.com", "correct-password"));

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Verify_MissingDeviceToken_Returns401_BeforeAnyCredentialCheck()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", deviceToken: null,
            new OperatorVerifyRequest("anyone@example.com", "anything"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_RevokedDeviceToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedOperatorAsync("verify-revokeddevice@example.com", "correct-password");
        var revokedToken = await IssueRevokedDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Post, "/device/operators/verify", revokedToken,
            new OperatorVerifyRequest("verify-revokeddevice@example.com", "correct-password"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- GET /device/operators/{userId}/status ------------------------------

    [Fact]
    public async Task Status_ActiveOperatorInScope_Returns200_Active()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOperatorAsync("status-active@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get, $"/device/operators/{userId}/status", deviceToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorStatusResponse>();
        Assert.Equal("active", body!.Status);
    }

    [Fact]
    public async Task Status_RevokedOperator_Returns200_Inactive_NotAnError()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOperatorAsync("status-revoked@example.com", "correct-password");
        await RevokeUserAsync(userId);
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get, $"/device/operators/{userId}/status", deviceToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorStatusResponse>();
        Assert.Equal("inactive", body!.Status);
    }

    [Fact]
    public async Task Status_UserFromAnotherOrganization_Returns200_Inactive_NeverLeaksExistence()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, _, foreignUserId) = await SeedOperatorAsync("status-foreign@example.com", "correct-password");
        var (orgId, branchId, _) = await SeedOperatorAsync("status-terminal-org@example.com", "correct-password");
        var deviceToken = await IssueDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get, $"/device/operators/{foreignUserId}/status", deviceToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OperatorStatusResponse>();
        Assert.Equal("inactive", body!.Status);
    }

    [Fact]
    public async Task Status_MissingDeviceToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get, $"/device/operators/{Guid.NewGuid()}/status", deviceToken: null);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_RevokedDeviceToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedOperatorAsync("status-revokeddevice@example.com", "correct-password");
        var revokedToken = await IssueRevokedDeviceTokenAsync(orgId, branchId);

        var client = _factory.CreateClient();
        var request = BuildRequest(HttpMethod.Get, $"/device/operators/{userId}/status", revokedToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
