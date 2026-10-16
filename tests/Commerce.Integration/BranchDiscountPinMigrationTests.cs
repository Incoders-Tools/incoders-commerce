using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0019_branch_discount_pin.sql` (branch-discount-pin spec): re-runnable, and
/// mirrored into the dev init snapshot with row-level security forced.
/// </summary>
[Collection("Postgres")]
public sealed class BranchDiscountPinMigrationTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string ScratchConnectionString(string dbName) =>
        new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;

    private static void Exec(string connectionString, string sql)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static bool TableIsForceRowSecured(NpgsqlConnection conn)
    {
        using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'branch_discount_pins' AND relrowsecurity AND relforcerowsecurity)", conn);
        return (bool)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Migration_IsRerunnable_AndForcesRowLevelSecurity()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        PostgresTestFixture.ApplyMigration(owner, "0019_branch_discount_pin.sql");
        PostgresTestFixture.ApplyMigration(owner, "0019_branch_discount_pin.sql");

        Assert.True(TableIsForceRowSecured(owner));
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheDiscountPinTable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dbName = "pin0019_" + Guid.NewGuid().ToString("N");
        Exec(PostgresTestFixture.OwnerConnectionString, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        try
        {
            using var conn = new NpgsqlConnection(ScratchConnectionString(dbName));
            conn.Open();
            var sql = File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using (var cmd = new NpgsqlCommand(sql, conn)) { cmd.ExecuteNonQuery(); }

            Assert.True(TableIsForceRowSecured(conn));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Exec(PostgresTestFixture.OwnerConnectionString, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }
}
