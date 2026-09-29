using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0018_catalog_categories.sql` (catalog-categories spec): the table, its
/// RLS, the backfill of existing products into one "Sin categoría" category
/// per organization, and the same-organization foreign key. Backfill needs a
/// genuine pre-0018 schema, which the shared `commerce_test` database can no
/// longer provide, so it runs in a throwaway database (the 0017 review-test
/// precedent in <see cref="MigrationRlsTests"/>).
/// </summary>
[Collection("Postgres")]
public sealed class CategoriesMigrationTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string ScratchConnectionString(string dbName) =>
        new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;

    private static void CreateDatabase(string dbName)
    {
        using var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        admin.Open();
        using var cmd = new NpgsqlCommand($"CREATE DATABASE {dbName} OWNER commerce_owner", admin);
        cmd.ExecuteNonQuery();
    }

    private static void DropDatabase(string dbName)
    {
        NpgsqlConnection.ClearAllPools();
        using var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        admin.Open();
        using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void ApplyMigrationsBefore0018(NpgsqlConnection conn)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            if (string.CompareOrdinal(file, "0018_catalog_categories.sql") >= 0)
            {
                break;
            }
            PostgresTestFixture.ApplyMigration(conn, file);
        }
    }

    [Fact]
    public void Migration_BackfillsSinCategoriaPerOrganizationWithProducts_AndIsRerunnable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var (orgA, orgB, orgC) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (branchA1, branchA2, branchB, branchC) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (productA1, productA2, productB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var actorId = Guid.NewGuid();
        var dbName = "cat0018_" + Guid.NewGuid().ToString("N");
        CreateDatabase(dbName);
        try
        {
            using var conn = new NpgsqlConnection(ScratchConnectionString(dbName));
            conn.Open();
            ApplyMigrationsBefore0018(conn);

            Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B'), ($3, 'C')", orgA, orgB, orgC);
            Exec(conn, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'A1'), ($3, $2, 'A2'), ($4, $5, 'B1'), ($6, $7, 'C1')",
                branchA1, orgA, branchA2, branchB, orgB, branchC, orgC);
            const string insertProduct =
                "INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id) VALUES ($1, $2, $3, $4, $5, $6, $7)";
            Exec(conn, insertProduct, productA1, orgA, branchA1, "Bife", Guid.NewGuid(), Guid.NewGuid(), actorId);
            Exec(conn, insertProduct, productA2, orgA, branchA2, "Pollo", Guid.NewGuid(), Guid.NewGuid(), actorId);
            Exec(conn, insertProduct, productB, orgB, branchB, "Vino", Guid.NewGuid(), Guid.NewGuid(), actorId);
            Exec(conn, "UPDATE products SET updated_at_utc = '2020-01-01T00:00:00Z'");

            PostgresTestFixture.ApplyMigration(conn, "0018_catalog_categories.sql");
            PostgresTestFixture.ApplyMigration(conn, "0018_catalog_categories.sql");

            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM categories WHERE organization_id = $1 AND name = 'Sin categoría'", orgA));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM categories WHERE organization_id = $1 AND name = 'Sin categoría'", orgB));
            Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM categories WHERE organization_id = $1", orgC));
            Assert.Equal(2L, Scalar<long>(conn, "SELECT count(*) FROM categories"));

            var orgACategory = Scalar<Guid>(conn, "SELECT id FROM categories WHERE organization_id = $1", orgA);
            Assert.Equal(2L, Scalar<long>(conn, "SELECT count(*) FROM products WHERE organization_id = $1 AND category_id = $2", orgA, orgACategory));
            var orgBCategory = Scalar<Guid>(conn, "SELECT id FROM categories WHERE organization_id = $1", orgB);
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM products WHERE organization_id = $1 AND category_id = $2", orgB, orgBCategory));

            // Rewritten products are marked changed so devices re-sync them.
            Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM products WHERE updated_at_utc < '2021-01-01T00:00:00Z'"));

            // The composite foreign key now refuses a category of another org.
            var foreign = Assert.Throws<PostgresException>(() => Exec(conn,
                "UPDATE products SET category_id = $1 WHERE id = $2", orgBCategory, productA1));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);

            // ... and refuses deleting a category that products still use.
            var inUse = Assert.Throws<PostgresException>(() => Exec(conn, "DELETE FROM categories WHERE id = $1", orgACategory));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, inUse.SqlState);
        }
        finally
        {
            DropDatabase(dbName);
        }
    }

    [Fact]
    public void Migration_EnforcesUniqueNamePerOrganization_AndTheFixedIconSet()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        var dbName = "cat0018_" + Guid.NewGuid().ToString("N");
        CreateDatabase(dbName);
        try
        {
            using var conn = new NpgsqlConnection(ScratchConnectionString(dbName));
            conn.Open();
            ApplyMigrationsBefore0018(conn);
            PostgresTestFixture.ApplyMigration(conn, "0018_catalog_categories.sql");
            Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);

            const string insert = "INSERT INTO categories (id, organization_id, name, icon_key) VALUES ($1, $2, $3, $4)";
            Exec(conn, insert, Guid.NewGuid(), orgA, "Carnes", "meat");
            Exec(conn, insert, Guid.NewGuid(), orgB, "Carnes", "meat");

            var duplicate = Assert.Throws<PostgresException>(() => Exec(conn, insert, Guid.NewGuid(), orgA, "carnes", "poultry"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

            var badIcon = Assert.Throws<PostgresException>(() => Exec(conn, insert, Guid.NewGuid(), orgA, "Otra", "spaceship"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, badIcon.SqlState);
        }
        finally
        {
            DropDatabase(dbName);
        }
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheCategoriesTableAndItsProductForeignKey()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var dbName = "cat0018_" + Guid.NewGuid().ToString("N");
        CreateDatabase(dbName);
        try
        {
            using var conn = new NpgsqlConnection(ScratchConnectionString(dbName));
            conn.Open();
            var sql = File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using (var cmd = new NpgsqlCommand(sql, conn)) { cmd.ExecuteNonQuery(); }

            Assert.True(Scalar<bool>(conn,
                "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'categories' AND relrowsecurity AND relforcerowsecurity)"));
            Assert.True(Scalar<bool>(conn,
                "SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'products_category_org_fk')"));
        }
        finally
        {
            DropDatabase(dbName);
        }
    }

    [Fact]
    public void Categories_AreRowLevelSecured_ByOrganization_ForAppRuntime()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            PostgresTestFixture.ApplyMigration(owner, "0018_catalog_categories.sql");
            Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);
            Exec(owner, "INSERT INTO categories (id, organization_id, name, icon_key) VALUES ($1, $2, 'Carnes', 'meat'), ($3, $4, 'Carnes', 'meat')",
                Guid.NewGuid(), orgA, Guid.NewGuid(), orgB);
        }

        using var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM categories WHERE name = 'Carnes'"));
            var crossOrgInsert = Assert.Throws<PostgresException>(() => Exec(app,
                "INSERT INTO categories (id, organization_id, name, icon_key) VALUES ($1, $2, 'Robada', 'meat')", Guid.NewGuid(), orgB));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossOrgInsert.SqlState);
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            // No scope at all: fail closed.
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM categories"));
            tx.Rollback();
        }
    }
}
