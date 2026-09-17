using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials task 3.6: a rename request whose body
/// carries forged actor fields (name kept for history/clarity even though
/// the current <c>RenameProductRequest</c> no longer HAS those fields — the
/// point of this test is that even sending them as extraneous JSON has zero
/// effect) is authorized using ONLY the store-loaded actor's real
/// roles/branch scope, never anything from the request body.
/// </summary>
[Collection("Postgres")]
public sealed class CatalogEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogEndpointTests(WebApplicationFactory<Program> factory)
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

        using var resetCmd = new NpgsqlCommand("TRUNCATE TABLE user_directory, users", owner);
        resetCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Test-only endpoint-free sign-in helper: creates a real user with the
    /// given real permission set and returns an authenticated HttpClient
    /// carrying the resulting cookie — proving the rename endpoint's
    /// authorization decision is driven ONLY by what is persisted for this
    /// user, not by anything the caller can inject into the rename request.
    /// </summary>
    private async Task<(HttpClient client, Guid organizationId, Guid branchId)> SignedInClientAsync(Permission permissions, Guid[]? branchScope = null)
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var created = await store.TryCreateAsync(
                new CloudTenantScope(organizationId),
                new NewUserAccount(userId, $"{userId}@example.com", "unused-hash", branchScope ?? new[] { branchId }, new[] { new RoleDto("test-role", permissions) }),
                CancellationToken.None);
            Assert.True(created);
        }

        return (await SignInViaTestEndpointAsync(organizationId, userId), organizationId, branchId);
    }

    private async Task<HttpClient> SignInViaTestEndpointAsync(Guid organizationId, Guid userId)
    {
        // Use the real cookie-issuing mechanism through a minimal local
        // sign-in shortcut: bootstrap a token-less path is unnecessary here
        // because AccountEndpoints only issues cookies after real password
        // verification. Instead, seed a known password and sign in for real.
        using (var scope = _factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            // The user was already created by SignedInClientAsync with an
            // "unused-hash" placeholder; overwrite with a real hash for a
            // known password so we can sign in for real over HTTP.
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), "test-password");
            using var cmd = new NpgsqlCommand("UPDATE users SET password_hash = $1 WHERE id = $2", owner);
            cmd.Parameters.AddWithValue(hash);
            cmd.Parameters.AddWithValue(userId);
            cmd.ExecuteNonQuery();

            using var emailCmd = new NpgsqlCommand("SELECT email FROM users WHERE id = $1", owner);
            emailCmd.Parameters.AddWithValue(userId);
            var email = (string)(await emailCmd.ExecuteScalarAsync())!;

            // BaseAddress MUST be https:// — Program.cs sets
            // CookieSecurePolicy.Always, so the auth cookie is only set/sent
            // on a request TestServer treats as secure.
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                BaseAddress = new Uri("https://localhost"),
            });
            var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, "test-password"));
            Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
            return client;
        }
    }

    [Fact]
    public async Task Rename_WithForgedActorFieldsInBody_IsIgnored_StoredActorPermissionsGovern_Allowed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, branchId) = await SignedInClientAsync(Permission.ManageCatalog);

        var productId = Guid.NewGuid();
        var forgedBody = JsonSerializer.Serialize(new
        {
            // Extraneous forged fields a naive deserializer might have honored
            // under the OLD RenameProductRequest shape — must have zero effect.
            actorId = Guid.NewGuid(),
            actorBranchScope = new[] { Guid.NewGuid() },
            actorRoles = new[] { new { name = "super-admin", permissions = 15 } },
            targetBranchId = branchId,
            currentName = "Old Name",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
            newName = "New Name",
            isOffline = false,
            correlationId = Guid.NewGuid(),
        });

        var response = await client.PostAsync(
            $"/catalog/products/{productId}/rename",
            new StringContent(forgedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rename_WhenStoredActorLacksPermission_Returns403_EvenWithForgedRoleClaim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, branchId) = await SignedInClientAsync(Permission.None);

        var productId = Guid.NewGuid();
        var forgedBody = JsonSerializer.Serialize(new
        {
            actorId = Guid.NewGuid(),
            actorRoles = new[] { new { name = "super-admin", permissions = 15 } },
            targetBranchId = branchId,
            currentName = "Old Name",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
            newName = "New Name",
            isOffline = false,
            correlationId = Guid.NewGuid(),
        });

        var response = await client.PostAsync(
            $"/catalog/products/{productId}/rename",
            new StringContent(forgedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
