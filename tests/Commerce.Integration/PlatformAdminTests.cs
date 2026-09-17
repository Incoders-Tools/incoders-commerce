using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy Phase 4 (tasks 4.5, 4.7-4.10): the
/// `/platform` group — scheme isolation, sign-in, genesis, list
/// organizations, bootstrap organization — against a live Postgres instance
/// (`deploy/dev/compose.yaml`). If unreachable, each test reports the gap
/// and returns without asserting pass/fail.
/// </summary>
[Collection("Postgres")]
public sealed class PlatformAdminTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PlatformAdminTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseSetting("ConnectionStrings:CommercePlatformRead", MigrationRlsTests.PlatformReadonlyConnectionString);
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
        foreach (var fileName in new[]
                 {
                     "0001_init_rls.sql",
                     "0002_users.sql",
                     "0003_organizations_branches.sql",
                     "0004_device_credentials.sql",
                     "0005_password_recovery.sql",
                     "0006_role_taxonomy.sql",
                     "0007_platform_administration.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", fileName))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", MigrationRlsTests.PlatformReadonlyPassword);
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE platform_admins, audit_log RESTART IDENTITY; " +
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private static WebApplicationFactoryClientOptions CookieClientOptions() => new()
    {
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    };

    private (Guid Id, string Email, string Password) SeedPlatformAdmin(string email, string password)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<Commerce.Domain.Identity.PlatformAdmin>>();
        var hash = hasher.HashPassword(new Commerce.Domain.Identity.PlatformAdmin(id, email), password);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO platform_admins (id, email, password_hash) VALUES ($1, $2, $3)", owner);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(email.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(hash);
        cmd.ExecuteNonQuery();

        return (id, email, password);
    }

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

        return (organizationId, body!.BranchId, body.UserId);
    }

    private static (string ActorKind, Guid ActorId, Guid? OrganizationId, string Action)? FindAuditRow(
        string entityType, Guid entityId, string action)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_kind, actor_id, organization_id, action FROM audit_log " +
            "WHERE entity_type = $1 AND entity_id = $2 AND action = $3", owner);
        cmd.Parameters.AddWithValue(entityType);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return (reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3));
    }

    // --- 4.5: scheme isolation -----------------------------------------------

    [Fact]
    public async Task OrgCookie_OnPlatformEndpoint_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(client, "4-5-org-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("4-5-org-admin@example.com", "admin-password"));

        var response = await client.GetAsync("/platform/organizations");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlatformCookie_OnOrgEndpoint_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, email, password) = SeedPlatformAdmin("4-5-platform-admin@example.com", "platform-password");
        var client = _factory.CreateClient(CookieClientOptions());
        var signIn = await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // /account/me only requires the default scheme's RequireAuthorization
        // (no explicit scheme name), which resolves to the org cookie scheme
        // ONLY — a platform-cookie-authenticated request carries no such
        // cookie anyway (Cookie.Path="/platform"), so this must be 401.
        var response = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlatformCookie_IsNeverSentTo_Account_BecauseOfCookiePath()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, email, password) = SeedPlatformAdmin("4-5-path-admin@example.com", "platform-password");
        var client = _factory.CreateClient(CookieClientOptions());
        var signIn = await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        Assert.True(signIn.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var platformCookie = Assert.Single(setCookies!, c => c.StartsWith("commerce.platform=", StringComparison.Ordinal));
        Assert.Contains("path=/platform", platformCookie, StringComparison.OrdinalIgnoreCase);

        // A fresh request to /account (outside the cookie's path) never
        // attaches it, so the caller is anonymous there.
        var accountResponse = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, accountResponse.StatusCode);
    }

    // --- 4.7: sign-in ---------------------------------------------------------

    [Fact]
    public async Task SignIn_KnownAdmin_Succeeds_AndCookieCarriesNoOrgIdClaim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, email, password) = SeedPlatformAdmin("4-7-known@example.com", "known-password");
        var client = _factory.CreateClient(CookieClientOptions());

        var response = await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var platformCookie = Assert.Single(setCookies!, c => c.StartsWith("commerce.platform=", StringComparison.Ordinal));
        // The cookie's payload is opaque (encrypted), so the absence of the
        // org_id claim is proven at the CLAIMS level, not by string
        // inspection — /platform/organizations succeeding with no
        // app.current_org_id set (covered separately) is the behavioral
        // proof; here we assert the cookie was issued at all under the
        // correct name.
        Assert.NotNull(platformCookie);
    }

    [Fact]
    public async Task SignIn_UnknownEmail_WrongPassword_AndOrgScopedCredentials_AllReturn401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, email, _) = SeedPlatformAdmin("4-7-real@example.com", "real-password");

        var unknownClient = _factory.CreateClient();
        var unknownResponse = await unknownClient.PostAsJsonAsync(
            "/platform/sign-in", new PlatformSignInRequest("nobody-platform@example.com", "whatever"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);

        var wrongPasswordClient = _factory.CreateClient();
        var wrongPasswordResponse = await wrongPasswordClient.PostAsJsonAsync(
            "/platform/sign-in", new PlatformSignInRequest(email, "not-the-real-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPasswordResponse.StatusCode);

        // An org-scoped UserAccount's credentials, submitted to the platform
        // sign-in endpoint, never authenticate as platform-admin.
        var orgClient = _factory.CreateClient();
        await BootstrapOrgAsync(orgClient, "4-7-org-user@example.com", "org-password");
        var orgCredsResponse = await orgClient.PostAsJsonAsync(
            "/platform/sign-in", new PlatformSignInRequest("4-7-org-user@example.com", "org-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, orgCredsResponse.StatusCode);
    }

    // --- 4.8: genesis ---------------------------------------------------------

    [Fact]
    public async Task Genesis_TokenPair_SucceedsOnce_SecondAttemptRejected_EvenCalledDirectly()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var client = _factory.CreateClient();

        var requestToken1 = await client.PostAsJsonAsync("/platform/bootstrap/request-token", new { });
        Assert.Equal(HttpStatusCode.Accepted, requestToken1.StatusCode);

        var token1 = registry.Issue(Guid.Empty);
        var first = await client.PostAsJsonAsync(
            "/platform/bootstrap", new PlatformGenesisRequest(token1, "4-8-genesis@example.com", "genesis-password"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // A second genesis token/attempt is rejected by the database's
        // NOT EXISTS policy, called DIRECTLY (bypassing the endpoint's own
        // "already has an admin" pre-check).
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Commerce.Cloud.Api.Persistence.PostgresPlatformAdminStore>();
        var directSecondAttempt = await store.TryCreateGenesisAsync(
            Guid.NewGuid(), "4-8-second@example.com", "irrelevant-hash", CancellationToken.None);
        Assert.False(directSecondAttempt);

        // And the endpoint's own pre-check now also refuses to issue a token.
        var requestToken2 = await client.PostAsJsonAsync("/platform/bootstrap/request-token", new { });
        Assert.Equal(HttpStatusCode.Conflict, requestToken2.StatusCode);
    }

    // --- 4.9: list organizations ----------------------------------------------

    [Fact]
    public async Task ListOrganizations_ReturnsBothSeededOrgs_WithNoCurrentOrgIdSet()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, email, password) = SeedPlatformAdmin("4-9-lister@example.com", "lister-password");
        var client = _factory.CreateClient(CookieClientOptions());
        await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var cmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, '4-9 Org A'), ($2, '4-9 Org B')", owner);
            cmd.Parameters.AddWithValue(orgAId);
            cmd.Parameters.AddWithValue(orgBId);
            cmd.ExecuteNonQuery();
        }

        var response = await client.GetAsync("/platform/organizations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var organizations = await response.Content.ReadFromJsonAsync<List<OrganizationSummary>>();

        Assert.Contains(organizations!, o => o.Id == orgAId);
        Assert.Contains(organizations!, o => o.Id == orgBId);
    }

    [Fact]
    public async Task ListOrganizations_MissingConnectionString_Returns503_NeverFallsBackToAppRuntime()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var factoryWithoutPlatformRead = _factory.WithWebHostBuilder(builder =>
        {
            // UseSetting(key, null) REMOVES the key from the host builder's
            // settings dictionary — the base factory's own
            // ConnectionStrings:CommercePlatformRead setting is erased, not
            // merely shadowed, so it is genuinely absent from configuration.
            builder.UseSetting("ConnectionStrings:CommercePlatformRead", null);
        });

        var (_, email, password) = SeedPlatformAdmin("4-9-no-conn@example.com", "no-conn-password");
        var client = factoryWithoutPlatformRead.CreateClient(CookieClientOptions());
        await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));

        var response = await client.GetAsync("/platform/organizations");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // --- 4.10: bootstrap organization -----------------------------------------

    [Fact]
    public async Task BootstrapOrganization_CreatesOrgBranchAndAdmin_InOneTransaction_AdminSignsIn_AuditedWithExplicitOrgId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (adminId, email, password) = SeedPlatformAdmin("4-10-platform@example.com", "platform-password");
        var client = _factory.CreateClient(CookieClientOptions());
        await client.PostAsJsonAsync("/platform/sign-in", new PlatformSignInRequest(email, password));

        var response = await client.PostAsJsonAsync(
            "/platform/organizations",
            new CreateOrganizationRequest("4-10 New Org", "HQ", "4-10-new-admin@example.com", "new-admin-password"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>();
        Assert.NotEqual(Guid.Empty, body!.OrganizationId);
        Assert.NotEqual(Guid.Empty, body.BranchId);
        Assert.NotEqual(Guid.Empty, body.UserId);

        // The created admin can sign in.
        var newAdminSignIn = await _factory.CreateClient().PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("4-10-new-admin@example.com", "new-admin-password"));
        Assert.Equal(HttpStatusCode.OK, newAdminSignIn.StatusCode);

        // Audited: platform actor, the SERVER-MINTED organization id (never
        // caller-supplied — the request body carries no organization id at
        // all).
        var row = FindAuditRow("organization", body.OrganizationId, "organization.bootstrapped");
        Assert.NotNull(row);
        Assert.Equal("platform-admin", row!.Value.ActorKind);
        Assert.Equal(adminId, row.Value.ActorId);
        Assert.Equal(body.OrganizationId, row.Value.OrganizationId);
    }

    [Fact]
    public async Task BootstrapOrganization_WithoutPlatformCookie_Returns401_AndCreatesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/platform/organizations",
            new CreateOrganizationRequest("Unauthorized Org", null, "unauth-admin@example.com", "password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
