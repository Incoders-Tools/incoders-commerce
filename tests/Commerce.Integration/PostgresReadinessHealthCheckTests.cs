using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials task 4.1: `/health/ready` must fail
/// closed when `users`/`user_directory` FORCE-RLS or policies are missing,
/// not just when `sync_inbox` is missing — the readiness gate must cover
/// every table, not only the one it originally shipped with.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresReadinessHealthCheckTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PostgresReadinessHealthCheckTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
        });
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

    [Fact]
    public async Task HealthReady_IsHealthy_WhenUsersAndUserDirectoryTablesExist_WithForcedRlsAndPolicies()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            var initSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0001_init_rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
            using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

            var usersSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0002_users.sql"));
            using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenUsersTablePolicyIsMissing_EvenThoughSyncInboxIsFine()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            var initSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0001_init_rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
            using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

            var usersSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0002_users.sql"));
            using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

            // Simulate a drifted/partial schema: drop only the users policy,
            // leaving the table and sync_inbox otherwise intact. The gate must
            // fail readiness even though sync_inbox is still fully correct.
            using var dropPolicyCmd = new NpgsqlCommand("DROP POLICY IF EXISTS users_tenant_isolation ON users", owner);
            dropPolicyCmd.ExecuteNonQuery();
        }

        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            // Restore the policy so later tests in this shared-Postgres
            // collection are unaffected.
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var usersSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0002_users.sql"));
            using var cmd = new NpgsqlCommand(usersSql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var initSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        foreach (var file in new[]
                 {
                     "0002_users.sql", "0003_organizations_branches.sql",
                     "0004_device_credentials.sql", "0005_password_recovery.sql",
                     "0006_role_taxonomy.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", file));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        var platformSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0007_platform_administration.sql"))
            .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        using (var cmd = new NpgsqlCommand(platformSql, owner)) cmd.ExecuteNonQuery();

        var customerSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0008_customer_registry.sql"));
        using (var cmd = new NpgsqlCommand(customerSql, owner)) cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-password-recovery task 1.8: `/health/ready` must fail
    /// closed when `password_reset_tokens` FORCE-RLS or its policies are
    /// missing, even though every other table is fully correct.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenPasswordResetTokensPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS password_reset_tokens_issue ON password_reset_tokens", owner);
            dropPolicyCmd.ExecuteNonQuery();
        }

        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0005_password_recovery.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenPasswordResetTokensTableExists_WithForcedRlsAndPolicies()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Covers commerce-role-taxonomy task 2.10: `/health/ready` must fail
    /// closed when `platform_admins`/`audit_log` FORCE-RLS or any expected
    /// policy is missing, even though every other table is fully correct.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenPlatformAdminsPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS platform_admins_genesis ON platform_admins", owner);
            dropPolicyCmd.ExecuteNonQuery();
        }

        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0007_platform_administration.sql"))
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenAuditLogPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand("DROP POLICY IF EXISTS audit_log_append ON audit_log", owner);
            dropPolicyCmd.ExecuteNonQuery();
        }

        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0007_platform_administration.sql"))
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenPlatformAdminsAndAuditLogExist_WithForcedRlsAndPolicies()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Covers commerce-customer-identity task 1.5: `/health/ready` must fail
    /// closed when `customers`/`customer_ordering_access` FORCE-RLS or any
    /// expected policy is missing, even though every other table is fully
    /// correct, and must pass once `0008` is (re-)applied.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenCustomersPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS customers_tenant_isolation ON customers", owner);
            dropPolicyCmd.ExecuteNonQuery();
        }

        try
        {
            var client = _factory.CreateClient();
            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0008_customer_registry.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenCustomersAndCustomerOrderingAccessExist_WithForcedRlsAndPolicies()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
        }

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
