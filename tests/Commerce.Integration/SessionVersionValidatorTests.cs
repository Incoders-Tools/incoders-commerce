using System.Security.Claims;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-password-recovery tasks 2.3/2.8: `SessionVersionValidator`
/// (the `OnValidatePrincipal` implementation) exercised directly against a
/// hand-built <see cref="CookieValidatePrincipalContext"/> — no HTTP
/// round-trip needed to prove the fail-closed / mismatch-rejection rules.
/// Against a LIVE Postgres instance for the DB-backed cache miss path.
/// </summary>
[Collection("Postgres")]
public sealed class SessionVersionValidatorTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public SessionVersionValidatorTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

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
        var root = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        foreach (var file in new[]
                 {
                     "0002_users.sql", "0003_organizations_branches.sql",
                     "0004_device_credentials.sql", "0005_password_recovery.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", file));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(CloudTenantScope Scope, Guid UserId)> SeedUserAsync()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using (var orgCmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", owner))
        {
            orgCmd.Parameters.AddWithValue(organizationId);
            orgCmd.ExecuteNonQuery();
        }
        using (var branchCmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", owner))
        {
            branchCmd.Parameters.AddWithValue(branchId);
            branchCmd.Parameters.AddWithValue(organizationId);
            branchCmd.ExecuteNonQuery();
        }

        var scope = new CloudTenantScope(organizationId);
        var userStore = new PostgresUserAccountStore(_dataSource!);
        await userStore.TryCreateAsync(
            scope,
            new NewUserAccount(userId, "validator@example.com", "hash", [branchId], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None);

        return (scope, userId);
    }

    private static CookieValidatePrincipalContext BuildContext(ClaimsPrincipal principal, IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var options = new CookieAuthenticationOptions();
        var ticket = new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme);
        return new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);
    }

    [Fact]
    public async Task ValidateAsync_MissingSessionVerClaim_RejectsPrincipal_FailClosed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var cache = new SessionVersionCache(store);
        var validator = new SessionVersionValidator(cache);
        var (scope, userId) = await SeedUserAsync();

        // Pre-change-shaped principal: org_id + NameIdentifier but NO
        // session_ver claim at all (the exact shape of a cookie issued before
        // this change).
        var claims = new[]
        {
            new Claim(TenantScopeResolver.OrganizationClaimType, scope.OrganizationId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var services = new ServiceCollection().BuildServiceProvider();
        var context = BuildContext(principal, services);

        var rejected = false;
        await validator.ValidateAsync(context, onReject: () => rejected = true);

        Assert.True(rejected);
    }

    [Fact]
    public async Task ValidateAsync_MatchingSessionVer_KeepsPrincipal()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var cache = new SessionVersionCache(store);
        var validator = new SessionVersionValidator(cache);
        var (scope, userId) = await SeedUserAsync();

        var claims = new[]
        {
            new Claim(TenantScopeResolver.OrganizationClaimType, scope.OrganizationId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("session_ver", "0"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var services = new ServiceCollection().BuildServiceProvider();
        var context = BuildContext(principal, services);

        var rejected = false;
        await validator.ValidateAsync(context, onReject: () => rejected = true);

        Assert.False(rejected);
    }

    [Fact]
    public async Task ValidateAsync_StaleSessionVer_RejectsPrincipal()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var store = new PostgresPasswordRecoveryStore(_dataSource!);
        var cache = new SessionVersionCache(store);
        var validator = new SessionVersionValidator(cache);
        var (scope, userId) = await SeedUserAsync();
        await store.SetPasswordAsync(scope, userId, "changed-hash", CancellationToken.None);

        var claims = new[]
        {
            new Claim(TenantScopeResolver.OrganizationClaimType, scope.OrganizationId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("session_ver", "0"), // stale: real DB value is now 1
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var services = new ServiceCollection().BuildServiceProvider();
        var context = BuildContext(principal, services);

        var rejected = false;
        await validator.ValidateAsync(context, onReject: () => rejected = true);

        Assert.True(rejected);
    }
}
