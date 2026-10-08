using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0041_take_orders_permission.sql` (staff-order-taking T1): every persisted `seller` and `business-admin` role entry
/// gains the TakeOrders bit (32); `cashier` and `provider` entries are untouched; a re-run is a no-op; the file is
/// mirrored verbatim in the dev init snapshot. Runs in a throwaway database so it never disturbs `commerce_test`.
/// </summary>
[Collection("Postgres")]
public sealed class TakeOrdersMigrationTests
{
    private const string MigrationFile = "0041_take_orders_permission.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string Scratch(string dbName) =>
        new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static int[] Permissions(NpgsqlConnection conn, Guid id)
    {
        using var cmd = new NpgsqlCommand(
            "SELECT array_agg((e->>'permissions')::int ORDER BY ord) FROM users, jsonb_array_elements(roles) WITH ORDINALITY AS r(e, ord) WHERE id = $1",
            conn);
        cmd.Parameters.AddWithValue(id);
        return (int[])cmd.ExecuteScalar()!;
    }

    private static void SeedUser(NpgsqlConnection conn, Guid id, string rolesJson) =>
        Exec(conn,
            "INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1, $2, $3, 'hash', '{}', $4::jsonb)",
            id, Guid.NewGuid(), $"{id:N}@example.com", rolesJson);

    [Fact]
    public void Migration_AddsTakeOrdersToSellerAndBusinessAdminOnly_AndIsIdempotent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dbName = "orders0041_" + Guid.NewGuid().ToString("N");
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

            var (admin, seller, cashier, mixed, already, none) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            SeedUser(conn, admin, """[{"name":"business-admin","permissions":31}]""");
            SeedUser(conn, seller, """[{"name":"seller","permissions":1}]""");
            SeedUser(conn, cashier, """[{"name":"cashier","permissions":16},{"name":"provider","permissions":0}]""");
            SeedUser(conn, mixed, """[{"name":"cashier","permissions":16},{"name":"seller","permissions":1}]""");
            SeedUser(conn, already, """[{"name":"business-admin","permissions":63}]""");
            SeedUser(conn, none, "[]");

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            Assert.Equal([63], Permissions(conn, admin));
            Assert.Equal([33], Permissions(conn, seller));
            Assert.Equal([16, 0], Permissions(conn, cashier));
            Assert.Equal([16, 33], Permissions(conn, mixed));
            Assert.Equal([63], Permissions(conn, already));

            var before = (Permissions(conn, admin), Permissions(conn, seller), Permissions(conn, cashier), Permissions(conn, mixed));
            PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            Assert.Equal(before.Item1, Permissions(conn, admin));
            Assert.Equal(before.Item2, Permissions(conn, seller));
            Assert.Equal(before.Item3, Permissions(conn, cashier));
            Assert.Equal(before.Item4, Permissions(conn, mixed));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            ExecAdmin($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    [Theory]
    [InlineData("0041_take_orders_permission.sql")]
    [InlineData("0042_staff_order_entry.sql")]
    public void Migration_IsMirroredVerbatimInTheDevInitSnapshot(string migration)
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", migration))), init);
    }

    private static void ExecAdmin(string sql)
    {
        using var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        admin.Open();
        Exec(admin, sql);
    }
}
