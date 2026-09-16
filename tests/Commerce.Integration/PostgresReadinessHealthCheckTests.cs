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
public sealed class PostgresReadinessHealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
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
}
