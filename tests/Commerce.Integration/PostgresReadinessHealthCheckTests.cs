using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                     "0006_role_taxonomy.sql", "0021_branch_codes.sql", "0022_terminal_registers.sql",
                     "0024_terminal_registers_assign_result.sql", "0025_orders.sql", "0026_orders_guest_check.sql",
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

        var catalogAndPricingSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0009_catalog_and_pricing.sql"));
        using (var cmd = new NpgsqlCommand(catalogAndPricingSql, owner)) cmd.ExecuteNonQuery();

        var guestOrderingSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0010_guest_ordering.sql"));
        using (var cmd = new NpgsqlCommand(guestOrderingSql, owner)) cmd.ExecuteNonQuery();

        var paymentsSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0011_payments.sql"));
        using (var cmd = new NpgsqlCommand(paymentsSql, owner)) cmd.ExecuteNonQuery();

        foreach (var file in new[] { "0013_rate_components.sql", "0014_rate_component_tenancy.sql" })
        {
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", file));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
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

    /// <summary>
    /// Covers commerce-pricing-engine task 1.6: `/health/ready` must fail
    /// closed when `products`/`presentations`/`price_lists`/
    /// `price_list_entries` FORCE-RLS or any expected policy is missing,
    /// even though every other table is fully correct, and must pass once
    /// `0009` is (re-)applied.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenProductsPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS products_tenant_isolation ON products", owner);
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
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0009_catalog_and_pricing.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenPriceListEntriesPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS price_list_entries_tenant_isolation ON price_list_entries", owner);
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
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0009_catalog_and_pricing.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenProductsPresentationsPriceListsAndEntriesExist_WithForcedRlsAndPolicies()
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
    /// Covers commerce-guest-ordering task 2.3: `/health/ready` must fail
    /// closed when `guest_order_verifications` FORCE-RLS or any expected
    /// policy is missing, even though every other table is fully correct,
    /// and must pass once `0010` is (re-)applied.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenGuestOrderVerificationsPolicyIsMissing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var dropPolicyCmd = new NpgsqlCommand(
                "DROP POLICY IF EXISTS guest_order_verifications_lookup ON guest_order_verifications", owner);
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
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0010_guest_ordering.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenGuestOrderVerificationsExists_WithForcedRlsAndPolicies()
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
    /// Covers Unit 4 task 4.6: `/health/ready` fails before `0011` is applied
    /// and passes after, asserting `payment_entries` exists with
    /// `relforcerowsecurity` and its policy.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_BeforePaymentsMigrationApplied()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            // Every migration EXCEPT 0011 — payment_entries deliberately absent.
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

            var catalogAndPricingSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0009_catalog_and_pricing.sql"));
            using (var cmd = new NpgsqlCommand(catalogAndPricingSql, owner)) cmd.ExecuteNonQuery();

            var guestOrderingSql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0010_guest_ordering.sql"));
            using (var cmd = new NpgsqlCommand(guestOrderingSql, owner)) cmd.ExecuteNonQuery();

            using var dropCmd = new NpgsqlCommand("DROP TABLE IF EXISTS payment_entries CASCADE", owner);
            dropCmd.ExecuteNonQuery();
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
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", "0011_payments.sql"));
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task HealthReady_IsHealthy_WhenPaymentEntriesExists_WithForcedRlsAndPolicy()
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
    /// R4-deploy-order-hard-dependency. Since commerce-price-composition slice
    /// 2, order pricing reads `rate_component_sets` on EVERY priced line. An
    /// API deployed ahead of migration `0013` therefore fails every line of
    /// every order with "relation does not exist", surfacing to the customer
    /// as an unexplained `no-effective-price` denial — a total ordering outage
    /// with no signal naming its cause.
    ///
    /// Readiness is where that becomes visible: the instance never takes
    /// traffic, and the unhealthy message names the migration to apply. The
    /// runbook half of this fix is in design.md; this is the half that does not
    /// depend on anyone having read it.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_BeforeRateComponentsMigrationApplied()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
            using var dropCmd = new NpgsqlCommand(
                "DROP TABLE IF EXISTS rate_components, rate_component_sets CASCADE", owner);
            dropCmd.ExecuteNonQuery();
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
            ApplyAllMigrations(owner);
        }
    }

    /// <summary>
    /// R4-001 (human document numbers). Pairing calls `terminal_registers_assign`
    /// on every request; an API deployed ahead of migration 0022 would answer
    /// every pairing with a 500. Readiness makes that visible instead.
    /// </summary>
    [Theory]
    [InlineData("DROP FUNCTION IF EXISTS terminal_registers_assign(uuid, uuid, uuid, boolean)")]
    [InlineData("DROP TABLE IF EXISTS terminal_registers CASCADE")]
    public async Task HealthReady_IsUnhealthy_BeforeTerminalRegistersMigrationApplied(string drift)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
            using var dropCmd = new NpgsqlCommand(drift, owner);
            dropCmd.ExecuteNonQuery();
        }

        try
        {
            var response = await _factory.CreateClient().GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            ApplyAllMigrations(owner);
        }
    }

    /// <summary>
    /// A database with 0022's scalar `terminal_registers_assign` but without the result shape of
    /// 0024 must not take traffic: the API reads `newly_allocated` on every pairing.
    /// </summary>
    [Fact]
    public async Task HealthReady_IsUnhealthy_WhenTheAssignFunctionLacksThe0024ResultShape_AndNamesTheMigration()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);

            using var downgrade = new NpgsqlCommand(
                "DROP FUNCTION terminal_registers_assign(uuid, uuid, uuid, boolean); " +
                "CREATE FUNCTION terminal_registers_assign(p_organization_id uuid, p_branch_id uuid, p_installation_id uuid, p_release_others boolean DEFAULT true) " +
                "RETURNS smallint LANGUAGE sql AS 'SELECT 1::smallint';", owner);
            downgrade.ExecuteNonQuery();
        }

        try
        {
            var logs = new CapturedLogs();
            var client = _factory
                .WithWebHostBuilder(b => b.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)))
                .CreateClient();

            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains(logs.Errors, message => message.Contains("migration 0021/0022/0024 missing", StringComparison.Ordinal));
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            ApplyAllMigrations(owner);
        }
    }

    /// <summary>
    /// persist-web-orders. Every order submission reads and writes `orders`/`order_lines`; an API
    /// deployed ahead of migration 0025 would answer each one with a 500 (and lose the order).
    /// Readiness makes that visible instead and names the migration.
    /// </summary>
    [Theory]
    [InlineData("DROP TABLE IF EXISTS order_lines, orders CASCADE")]
    [InlineData("DROP TABLE IF EXISTS order_lines")]
    [InlineData("DROP POLICY IF EXISTS orders_tenant_isolation ON orders")]
    [InlineData("DROP POLICY IF EXISTS order_lines_tenant_isolation ON order_lines")]
    [InlineData("ALTER TABLE orders NO FORCE ROW LEVEL SECURITY")]
    public async Task HealthReady_IsUnhealthy_BeforeOrdersMigrationApplied_AndNamesTheMigration(string drift)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            ApplyAllMigrations(owner);
            using var dropCmd = new NpgsqlCommand(drift, owner);
            dropCmd.ExecuteNonQuery();
        }

        try
        {
            var logs = new CapturedLogs();
            var client = _factory
                .WithWebHostBuilder(b => b.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(logs)))
                .CreateClient();

            var response = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains(logs.Errors, message => message.Contains("migration 0025 missing", StringComparison.Ordinal));
        }
        finally
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            ApplyAllMigrations(owner);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public ILogger CreateLogger(string categoryName) => new Sink(_errors);

        public void Dispose() { }

        private sealed class Sink(System.Collections.Concurrent.ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Error) errors.Enqueue(formatter(state, exception));
            }
        }
    }
}
