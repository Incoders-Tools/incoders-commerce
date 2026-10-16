using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0020_operate_pos_permission.sql` (pos-operator-session "Operating The POS
/// Requires OperatePos"): every persisted `business-admin` role entry gains
/// the OperatePos bit (16); other roles are untouched; a re-run is a no-op.
/// Runs in a throwaway database so it never disturbs the shared
/// `commerce_test` schema.
/// </summary>
[Collection("Postgres")]
public sealed class OperatePosMigrationTests
{
    private const string MigrationFile = "0020_operate_pos_permission.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string Scratch(string dbName) =>
        new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static string Roles(NpgsqlConnection conn, Guid id)
    {
        using var cmd = new NpgsqlCommand("SELECT roles::text FROM users WHERE id = $1", conn);
        cmd.Parameters.AddWithValue(id);
        return (string)cmd.ExecuteScalar()!;
    }

    private static void SeedUser(NpgsqlConnection conn, Guid id, string rolesJson) =>
        Exec(conn,
            "INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1, $2, $3, 'hash', '{}', $4::jsonb)",
            id, Guid.NewGuid(), $"{id:N}@example.com", rolesJson);

    [Fact]
    public void Migration_AddsOperatePosToBusinessAdminOnly_AndIsIdempotent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dbName = "pos0020_" + Guid.NewGuid().ToString("N");
        ExecAdmin($"CREATE DATABASE {dbName} OWNER commerce_owner");
        try
        {
            using var conn = new NpgsqlConnection(Scratch(dbName));
            conn.Open();
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>()
                         .Where(f => string.CompareOrdinal(f, MigrationFile) < 0).OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(conn, file);
            }

            var (admin, seller, mixed, already) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            SeedUser(conn, admin, """[{"name":"business-admin","permissions":15}]""");
            SeedUser(conn, seller, """[{"name":"seller","permissions":1}]""");
            SeedUser(conn, mixed, """[{"name":"seller","permissions":1},{"name":"business-admin","permissions":15}]""");
            SeedUser(conn, already, """[{"name":"business-admin","permissions":31}]""");

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            Assert.Contains("\"permissions\": 31", Roles(conn, admin));
            Assert.Contains("\"permissions\": 1}", Roles(conn, seller));
            Assert.DoesNotContain("31", Roles(conn, seller));
            var mixedRoles = Roles(conn, mixed);
            Assert.Contains("\"permissions\": 31", mixedRoles);
            Assert.Contains("\"name\": \"seller\"", mixedRoles);
            Assert.Contains("\"permissions\": 31", Roles(conn, already));

            var before = (Roles(conn, admin), Roles(conn, seller), Roles(conn, mixed), Roles(conn, already));
            PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            var after = (Roles(conn, admin), Roles(conn, seller), Roles(conn, mixed), Roles(conn, already));
            Assert.Equal(before, after);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            ExecAdmin($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    private static void ExecAdmin(string sql)
    {
        using var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        admin.Open();
        Exec(admin, sql);
    }
}
