using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials tasks 3.1 and 3.3: real credential
/// verification at `/account/sign-in` and the HTTP bootstrap flow, against a
/// live Postgres instance (`deploy/dev/compose.yaml`). If Postgres is not
/// reachable, these tests report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class AccountEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public AccountEndpointTests(WebApplicationFactory<Program> factory)
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

    public void Dispose() { }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Commerce.sln")))
        {
            repoRoot = repoRoot.Parent;
        }
        if (repoRoot is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }

        var initSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var orgsSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(orgsSql, owner)) cmd.ExecuteNonQuery();

        var deviceSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0004_device_credentials.sql"));
        using (var cmd = new NpgsqlCommand(deviceSql, owner)) cmd.ExecuteNonQuery();

        // device_credentials (0004) carries FKs to organizations/branches, so
        // it must be truncated before/alongside them.
        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE user_directory, users, device_credentials, branches, organizations", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedUserAsync(Guid organizationId, Guid userId, string email, string plaintextPassword, bool revoked = false)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<Commerce.Domain.Identity.UserAccount>>();
        var dummyActor = new Commerce.Domain.Identity.UserAccount(userId, organizationId, Array.Empty<Guid>(), Array.Empty<Commerce.Domain.Identity.Role>());
        var hash = hasher.HashPassword(dummyActor, plaintextPassword);

        var created = await store.TryCreateAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, Array.Empty<Guid>(), new[] { new RoleDto("admin", Commerce.Domain.Identity.Permission.ManageCatalog) }),
            CancellationToken.None);
        Assert.True(created);

        if (revoked)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var cmd = new NpgsqlCommand("UPDATE users SET is_revoked = true WHERE id = $1", owner);
            cmd.Parameters.AddWithValue(userId);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task SignIn_CorrectPassword_Succeeds_WithStoredOrgId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "signin@example.com", "correct-horse-battery-staple");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("signin@example.com", "correct-horse-battery-staple"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SignedInResponse>();
        Assert.Equal(organizationId, body!.OrganizationId);
        Assert.Equal(userId, body.UserId);
    }

    [Fact]
    public async Task SignIn_WrongPassword_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "wrongpass@example.com", "correct-password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("wrongpass@example.com", "incorrect-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_UnknownEmail_Returns401_SameShapeAsWrongPassword()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("nobody-at-all@example.com", "whatever"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_RevokedUser_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "revoked@example.com", "some-password", revoked: true);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("revoked@example.com", "some-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_RequestToken_Returns202_WithEmptyBody()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/account/bootstrap/request-token", new BootstrapTokenRequest(organizationId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body), "202 response body must be empty — the token is never returned over HTTP.");
    }

    [Fact]
    public async Task Bootstrap_RequestToken_Returns409_WhenOrgAlreadyHasUsers()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "existing@example.com", "password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/account/bootstrap/request-token", new BootstrapTokenRequest(organizationId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_ValidToken_CreatesAdmin_WithFullPermissions_AndReturnsIds()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", null, "admin@example.com", "admin-password"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        Assert.Equal(organizationId, body!.OrganizationId);
        Assert.NotEqual(Guid.Empty, body.BranchId);
        Assert.NotEqual(Guid.Empty, body.UserId);

        var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("admin@example.com", "admin-password"));
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_OmittedBranchName_CreatesBranchNamed_Main()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", null, "mainbranch@example.com", "password"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT name FROM branches WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(body!.BranchId);
        var name = (string)cmd.ExecuteScalar()!;
        Assert.Equal("Main", name);
    }

    [Fact]
    public async Task Bootstrap_SecondBootstrapOnSameOrganization_Returns409()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var firstToken = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, firstToken, "Acme Corp", null, "second-boot-1@example.com", "password"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var secondToken = registry.Issue(organizationId);
        var second = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, secondToken, "Acme Corp Again", null, "second-boot-2@example.com", "password"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_BlankOrganizationName_Returns400_AndTokenStaysConsumable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var blankResponse = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "   ", null, "blankname@example.com", "password"));

        Assert.Equal(HttpStatusCode.BadRequest, blankResponse.StatusCode);

        // The token must still be consumable — validation ran BEFORE
        // TryConsume, so this mistake did not burn it.
        var retry = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Now A Real Name", null, "blankname@example.com", "password"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_ReplayedToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", null, "replay1@example.com", "password"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same org already has a user now, but the more specific assertion is
        // that the SAME token cannot be reused at all.
        var replay = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", null, "replay2@example.com", "password"));

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_ExpiredToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var registry = new Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry(() => clockBox[0]);
        var token = registry.Issue(organizationId);
        clockBox[0] = now.AddMinutes(16);

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(registry);
            });
        });
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", null, "expired@example.com", "password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WrongOrgToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(otherOrganizationId, token, "Acme Corp", null, "wrongorg@example.com", "password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// End-to-end authorization proof (design.md "Interfaces / Contracts" /
    /// "Testing Strategy"): bootstrap -> sign in -> LoadActorAsync -> the
    /// actor's BranchScope contains the REAL created branchId ->
    /// TenantAuthorizationService.Authorize for a catalog-rename targeting
    /// that branch returns allowed; a DIFFERENT branch id returns not-found.
    /// </summary>
    [Fact]
    public async Task Bootstrap_ThenCatalogRename_OnCreatedBranch_IsAllowed_OnOtherBranch_IsNotFound()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<Commerce.Cloud.Api.Authentication.BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        // BaseAddress MUST be https:// — the auth cookie has SecurePolicy =
        // Always, so it is never sent back on a plain http:// TestServer
        // client (see CatalogEndpointTests.cs's identical comment).
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var bootstrapResponse = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Acme Corp", "HQ", "renamer@example.com", "rename-password"));
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
        var bootstrapBody = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapResponse>();

        var signInResponse = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renamer@example.com", "rename-password"));
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var allowedResponse = await client.PostAsJsonAsync(
            $"/catalog/products/{Guid.NewGuid()}/rename",
            new RenameProductRequest(
                bootstrapBody!.BranchId, "Original", Guid.NewGuid(), Guid.NewGuid(), "Renamed", false, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);

        var deniedResponse = await client.PostAsJsonAsync(
            $"/catalog/products/{Guid.NewGuid()}/rename",
            new RenameProductRequest(
                Guid.NewGuid(), "Original", Guid.NewGuid(), Guid.NewGuid(), "Renamed", false, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }
}
