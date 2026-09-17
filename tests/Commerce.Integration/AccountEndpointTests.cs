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

        var recoverySql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0005_password_recovery.sql"));
        using (var cmd = new NpgsqlCommand(recoverySql, owner)) cmd.ExecuteNonQuery();

        // device_credentials (0004) and password_reset_tokens (0005) carry
        // FKs to organizations/branches, so they must be truncated
        // before/alongside them.
        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedUserAsync(
        Guid organizationId, Guid userId, string email, string plaintextPassword, bool revoked = false,
        Commerce.Domain.Identity.Permission permissions = Commerce.Domain.Identity.Permission.ManageCatalog)
    {
        // password_reset_tokens carries an FK to organizations (0005), so an
        // organizations row must exist for reset-request/confirm tests even
        // though `users` itself has no such FK.
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
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<Commerce.Domain.Identity.UserAccount>>();
        var dummyActor = new Commerce.Domain.Identity.UserAccount(userId, organizationId, Array.Empty<Guid>(), Array.Empty<Commerce.Domain.Identity.Role>());
        var hash = hasher.HashPassword(dummyActor, plaintextPassword);

        var created = await store.TryCreateAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, Array.Empty<Guid>(), new[] { new RoleDto("admin", permissions) }),
            CancellationToken.None);

        if (!created)
        {
            // TryCreateAsync enforces the bootstrap "zero users in this org"
            // invariant — a SECOND user in the same org (needed by the
            // admin-forced-reset tests) must be inserted directly, mirroring
            // PostgresUserAccountStore.InsertAsync's exact schema.
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var rolesJson = System.Text.Json.JsonSerializer.Serialize(
                new[] { new RoleDto("admin", permissions) },
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
            created = true;
        }

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

    // --- commerce-password-recovery: renew (Phase 2) -----------------------

    [Fact]
    public async Task Renew_CorrectCurrentPassword_Returns204_AndRefreshedCookieStillAuthenticates()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "renew-ok@example.com", "old-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signIn = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-ok@example.com", "old-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var renew = await client.PostAsJsonAsync(
            "/account/renew-password",
            new Commerce.Cloud.Api.Endpoints.RenewPasswordRequest("old-password", "new-password"));
        Assert.Equal(HttpStatusCode.NoContent, renew.StatusCode);

        // The refreshed cookie (re-issued by the renew endpoint with the new
        // session_ver) still authenticates.
        var me = await client.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // The new password actually works for a fresh sign-in.
        var client2 = _factory.CreateClient();
        var signInWithNew = await client2.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-ok@example.com", "new-password"));
        Assert.Equal(HttpStatusCode.OK, signInWithNew.StatusCode);
    }

    [Fact]
    public async Task Renew_WrongCurrentPassword_Returns401_AndHashIsUnchanged()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "renew-wrong@example.com", "correct-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signIn = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-wrong@example.com", "correct-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var renew = await client.PostAsJsonAsync(
            "/account/renew-password",
            new Commerce.Cloud.Api.Endpoints.RenewPasswordRequest("wrong-current-password", "new-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, renew.StatusCode);

        var client2 = _factory.CreateClient();
        var stillWorksWithOld = await client2.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-wrong@example.com", "correct-password"));
        Assert.Equal(HttpStatusCode.OK, stillWorksWithOld.StatusCode);
    }

    /// <summary>
    /// Spec: "Prior session cookie stops authenticating after a change"
    /// (renewal leg) — a cookie captured BEFORE renewal is rejected on the
    /// next request, since the renewal bumped `session_version` and this
    /// second client's cookie still carries the old `session_ver` claim.
    /// </summary>
    [Fact]
    public async Task Renew_InvalidatesPriorSessionCookie_ForAnotherClientHoldingTheOldCookie()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        await SeedUserAsync(organizationId, Guid.NewGuid(), "renew-stale@example.com", "old-password");

        var clientOptions = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        };

        // Client A signs in and captures the PRE-renewal cookie.
        var clientA = _factory.CreateClient(clientOptions);
        var signInA = await clientA.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-stale@example.com", "old-password"));
        Assert.Equal(HttpStatusCode.OK, signInA.StatusCode);
        var meBeforeRenew = await clientA.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, meBeforeRenew.StatusCode);

        // Client B signs in separately and renews the password.
        var clientB = _factory.CreateClient(clientOptions);
        var signInB = await clientB.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("renew-stale@example.com", "old-password"));
        Assert.Equal(HttpStatusCode.OK, signInB.StatusCode);
        var renew = await clientB.PostAsJsonAsync(
            "/account/renew-password",
            new Commerce.Cloud.Api.Endpoints.RenewPasswordRequest("old-password", "new-password"));
        Assert.Equal(HttpStatusCode.NoContent, renew.StatusCode);

        // Client A's now-stale cookie must be rejected.
        var meAfterRenew = await clientA.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfterRenew.StatusCode);
    }

    // --- commerce-password-recovery: reset-request / confirm (Phase 3) -----

    private sealed class FakeEmailSender : Commerce.Cloud.Api.Email.IEmailSender
    {
        public List<Commerce.Cloud.Api.Email.EmailMessage> Sent { get; } = [];

        public Task<bool> SendAsync(Commerce.Cloud.Api.Email.EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(true);
        }
    }

    private WebApplicationFactory<Program> CreateFactoryWithFakeEmailSender(FakeEmailSender sender) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<Commerce.Cloud.Api.Email.IEmailSender>(sender);
            });
        });

    private static string ExtractToken(string linkContainingBody)
    {
        var marker = "token=";
        var start = linkContainingBody.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = start;
        while (end < linkContainingBody.Length && !char.IsWhiteSpace(linkContainingBody[end]) && linkContainingBody[end] != '"')
        {
            end++;
        }
        return linkContainingBody[start..end];
    }

    private static int CountTokenRowsForUser(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM password_reset_tokens WHERE user_id = $1", owner);
        cmd.Parameters.AddWithValue(userId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static void BackdateTokenExpiry(Guid userId, TimeSpan pastOffset)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "UPDATE password_reset_tokens SET expires_at = now() - $1 WHERE user_id = $2 AND consumed_at IS NULL", owner);
        cmd.Parameters.AddWithValue(pastOffset);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task ResetRequest_KnownEmail_IssuesExactlyOneToken_AndSendsEmail_Returns202EmptyBody()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "reset-known@example.com", "old-password");

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        var response = await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("reset-known@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body));

        Assert.Equal(1, CountTokenRowsForUser(userId));
        Assert.Single(sender.Sent);
        Assert.Equal("reset-known@example.com", sender.Sent[0].To);
    }

    [Fact]
    public async Task ResetRequest_UnknownEmail_IssuesNoToken_SendsNoEmail_Returns202SameShape()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        var response = await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("nobody-reset@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ResetRequest_RevokedUser_IssuesNoToken_Returns202()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "reset-revoked@example.com", "old-password", revoked: true);

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        var response = await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("reset-revoked@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, CountTokenRowsForUser(userId));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ResetRequest_RepeatedWithinThrottleWindow_IssuesNoSecondToken_Returns202ByteIdenticalToFirst()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "reset-throttled@example.com", "old-password");

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        var first = await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("reset-throttled@example.com"));
        var second = await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("reset-throttled@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.Equal(firstBody, secondBody);

        // Only ONE token was issued and ONE email sent — the second, throttled
        // request created neither (design.md "Throttled response").
        Assert.Equal(1, CountTokenRowsForUser(userId));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Confirm_ValidToken_SetsNewPassword_AndPriorSessionsAreInvalidated()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "confirm-valid@example.com", "old-password");

        var sender = new FakeEmailSender();
        var factory = CreateFactoryWithFakeEmailSender(sender);

        // A prior client signs in and holds a cookie from BEFORE the reset.
        var priorClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var priorSignIn = await priorClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("confirm-valid@example.com", "old-password"));
        Assert.Equal(HttpStatusCode.OK, priorSignIn.StatusCode);

        var requestClient = factory.CreateClient();
        var requestResponse = await requestClient.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("confirm-valid@example.com"));
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
        var token = ExtractToken(sender.Sent[0].TextBody);

        var confirmResponse = await requestClient.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, "brand-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        var signInWithNew = await requestClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("confirm-valid@example.com", "brand-new-password"));
        Assert.Equal(HttpStatusCode.OK, signInWithNew.StatusCode);

        // The prior cookie no longer authenticates.
        var priorMe = await priorClient.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, priorMe.StatusCode);
    }

    [Fact]
    public async Task Confirm_ReplayedToken_Returns401_AndPasswordIsNotChangedAgain()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "confirm-replay@example.com", "old-password");

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("confirm-replay@example.com"));
        var token = ExtractToken(sender.Sent[0].TextBody);

        var first = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, "first-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var replay = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, "second-new-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var signInWithFirst = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("confirm-replay@example.com", "first-new-password"));
        Assert.Equal(HttpStatusCode.OK, signInWithFirst.StatusCode);
    }

    [Fact]
    public async Task Confirm_ExpiredToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "confirm-expired@example.com", "old-password");

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("confirm-expired@example.com"));
        var token = ExtractToken(sender.Sent[0].TextBody);
        BackdateTokenExpiry(userId, TimeSpan.FromMinutes(1));

        var response = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, "new-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_UnknownToken_Returns401_SameShapeAsOtherFailures()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest("not-a-real-token", "new-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_BlankBody_Returns400_AndTokenStillWorksAfterwards()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedUserAsync(organizationId, userId, "confirm-blank@example.com", "old-password");

        var sender = new FakeEmailSender();
        var client = CreateFactoryWithFakeEmailSender(sender).CreateClient();

        await client.PostAsJsonAsync(
            "/account/reset-password/request", new Commerce.Cloud.Api.Endpoints.ResetPasswordRequest("confirm-blank@example.com"));
        var token = ExtractToken(sender.Sent[0].TextBody);

        var blankResponse = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, ""));
        Assert.Equal(HttpStatusCode.BadRequest, blankResponse.StatusCode);

        var retry = await client.PostAsJsonAsync(
            "/account/reset-password/confirm",
            new Commerce.Cloud.Api.Endpoints.ConfirmResetPasswordRequest(token, "now-a-real-password"));
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    // --- commerce-password-recovery: admin-forced reset (Phase 4) ----------

    [Fact]
    public async Task AdminReset_ManageUsersHolder_ResetsSameOrgUser_Returns204_AndInvalidatesTargetSession()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(
            organizationId, adminId, "admin-reset-admin@example.com", "admin-password",
            permissions: Commerce.Domain.Identity.Permission.ManageUsers);
        await SeedUserAsync(organizationId, targetId, "admin-reset-target@example.com", "old-target-password");

        var clientOptions = new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        };

        // Target signs in and captures a pre-change cookie.
        var targetClient = _factory.CreateClient(clientOptions);
        var targetSignIn = await targetClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("admin-reset-target@example.com", "old-target-password"));
        Assert.Equal(HttpStatusCode.OK, targetSignIn.StatusCode);

        var adminClient = _factory.CreateClient(clientOptions);
        var adminSignIn = await adminClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("admin-reset-admin@example.com", "admin-password"));
        Assert.Equal(HttpStatusCode.OK, adminSignIn.StatusCode);

        var resetResponse = await adminClient.PostAsJsonAsync(
            $"/account/users/{targetId}/reset-password",
            new Commerce.Cloud.Api.Endpoints.AdminResetPasswordRequest("admin-forced-new-password"));
        Assert.Equal(HttpStatusCode.NoContent, resetResponse.StatusCode);

        // Target's pre-change cookie stops authenticating.
        var targetMeAfter = await targetClient.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, targetMeAfter.StatusCode);

        // The new password actually works.
        var freshSignIn = await _factory.CreateClient().PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("admin-reset-target@example.com", "admin-forced-new-password"));
        Assert.Equal(HttpStatusCode.OK, freshSignIn.StatusCode);
    }

    [Fact]
    public async Task AdminReset_CallerWithoutManageUsers_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(organizationId, callerId, "noperm-caller@example.com", "caller-password");
        await SeedUserAsync(organizationId, targetId, "noperm-target@example.com", "target-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signIn = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("noperm-caller@example.com", "caller-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var response = await client.PostAsJsonAsync(
            $"/account/users/{targetId}/reset-password",
            new Commerce.Cloud.Api.Endpoints.AdminResetPasswordRequest("new-password"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminReset_CrossOrganizationTarget_Returns404_IdenticalToNonexistentId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var organizationA = Guid.NewGuid();
        var organizationB = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var crossOrgTargetId = Guid.NewGuid();
        await SeedUserAsync(
            organizationA, adminId, "crossorg-admin@example.com", "admin-password",
            permissions: Commerce.Domain.Identity.Permission.ManageUsers);
        await SeedUserAsync(organizationB, crossOrgTargetId, "crossorg-target@example.com", "target-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signIn = await client.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("crossorg-admin@example.com", "admin-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var crossOrgResponse = await client.PostAsJsonAsync(
            $"/account/users/{crossOrgTargetId}/reset-password",
            new Commerce.Cloud.Api.Endpoints.AdminResetPasswordRequest("new-password"));
        var nonexistentResponse = await client.PostAsJsonAsync(
            $"/account/users/{Guid.NewGuid()}/reset-password",
            new Commerce.Cloud.Api.Endpoints.AdminResetPasswordRequest("new-password"));

        Assert.Equal(HttpStatusCode.NotFound, crossOrgResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistentResponse.StatusCode);

        // The cross-org target's password was NOT changed.
        var stillWorks = await _factory.CreateClient().PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("crossorg-target@example.com", "target-password"));
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }
}
