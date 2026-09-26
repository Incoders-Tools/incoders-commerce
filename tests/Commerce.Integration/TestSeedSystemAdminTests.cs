using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// B1 (odd/tasks/frontend-modernization.md, product review backlog): the
/// Users screen showed the platform sysadmin with `business-admin` checked
/// because `POST /internal/test-seed/user` always granted it that role.
/// This covers the fixed seam directly over HTTP — `systemAdmin: true` must
/// grant ZERO organization roles and ZERO branch scope, and still set
/// `is_system_admin = true`, mirroring
/// <see cref="TestSeedGuestVerificationCodeTests"/>'s WebApplicationFactory
/// pattern (Development environment, live Postgres).
/// </summary>
[Collection("Postgres")]
public sealed class TestSeedSystemAdminTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public TestSeedSystemAdminTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseEnvironment("Development");
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
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
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
        Apply("0009_catalog_and_pricing.sql");
        Apply("0010_guest_ordering.sql");
        Apply("0011_payments.sql");
        // Adds `users.is_system_admin`, which the assertions below read
        // directly and which sign-in reports back as `isSystemAdmin`.
        Apply("0012_admin_console.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE payment_entries, guest_order_verifications, price_list_entries, price_lists, presentations, " +
            "products, customer_ordering_access, customers, password_reset_tokens, user_directory, users, " +
            "device_credentials, branches, organizations RESTART IDENTITY CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task SystemAdminTrue_GrantsZeroRolesAndZeroBranchScope_AndSetsIsSystemAdmin()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var organizationId = Guid.NewGuid();
        const string email = "sysadmin-seed@example.com";
        const string password = "correct-horse-battery-staple";

        var seedResponse = await client.PostAsJsonAsync(
            "/internal/test-seed/user",
            new TestSeedUserRequest(organizationId, email, password, SystemAdmin: true));

        Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);
        var seeded = await seedResponse.Content.ReadFromJsonAsync<TestSeedUserResponse>();
        Assert.NotNull(seeded);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT is_system_admin, roles, branch_scope FROM users WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(seeded!.UserId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("[]", reader.GetString(1));
        Assert.Empty(reader.GetFieldValue<Guid[]>(2));

        // Real proof the flag round-trips through the actual sign-in path,
        // not just the raw column.
        var signInResponse = await client.PostAsJsonAsync(
            "/account/sign-in", new { email, password });
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
        var signedIn = await signInResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(signedIn.GetProperty("isSystemAdmin").GetBoolean());
        Assert.Equal(0, signedIn.GetProperty("permissions").GetInt32());
    }

    /// <summary>
    /// Regression guard: the pre-existing E2E-facing shape (`systemAdmin`
    /// omitted, exactly what `seedUser` in
    /// `src/Commerce.Web/e2e/helpers.ts` sends) must keep granting
    /// business-admin over the seeded branch, unchanged.
    /// </summary>
    [Fact]
    public async Task SystemAdminOmitted_StillGrantsBusinessAdminOverTheSeededBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var organizationId = Guid.NewGuid();
        const string email = "ordinary-seed@example.com";
        const string password = "correct-horse-battery-staple";

        var seedResponse = await client.PostAsJsonAsync(
            "/internal/test-seed/user",
            new TestSeedUserRequest(organizationId, email, password));

        Assert.Equal(HttpStatusCode.OK, seedResponse.StatusCode);
        var seeded = await seedResponse.Content.ReadFromJsonAsync<TestSeedUserResponse>();
        Assert.NotNull(seeded);
        Assert.NotEqual(Guid.Empty, seeded!.BranchId);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT is_system_admin, roles, branch_scope FROM users WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(seeded.UserId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.GetBoolean(0));
        Assert.Contains("business-admin", reader.GetString(1));
        Assert.Equal(new[] { seeded.BranchId }, reader.GetFieldValue<Guid[]>(2));
    }
}
