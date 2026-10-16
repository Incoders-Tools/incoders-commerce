using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0029_customer_contacts.sql`: the organization-scoped `customer_contacts`
/// sub-table (several contacts per customer, at most one primary), the move of
/// `customers.contact_name` into it and the removal of that column.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerContactsMigrationTests
{
    private const string MigrationFile = "0029_customer_contacts.sql";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string MigrationsDir => Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");

    private static void ApplyAllMigrations(NpgsqlConnection owner, Func<string, bool>? include = null)
    {
        foreach (var file in Directory.GetFiles(MigrationsDir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            if (include is null || include(file)) PostgresTestFixture.ApplyMigration(owner, file);
        }
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
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)value;
    }

    private static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
    }

    private static void WithScratchDatabase(Action<NpgsqlConnection> body)
    {
        var dbName = "contacts0029_" + Guid.NewGuid().ToString("N");
        using (var admin = OpenOwner()) Exec(admin, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            body(conn);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = OpenOwner();
            Exec(admin, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    private static (Guid Org, Guid Customer) SeedCustomer(NpgsqlConnection conn, string name = "Cliente")
    {
        var org = Guid.NewGuid();
        var customer = Guid.NewGuid();
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "Org " + org);
        Exec(conn, "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id) VALUES ($1, $2, 'Retail', $3, $4)",
            customer, org, name, Guid.NewGuid());
        return (org, customer);
    }

    [Fact]
    public void Migration_CreatesTheTable_WithForcedRls_IsReRunnable_AndDropsContactName()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        ApplyAllMigrations(owner); // re-runnable, including 0027 after contact_name was retired

        Assert.True(Scalar<bool>(owner,
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = 'customer_contacts'"));
        Assert.False(Scalar<bool>(owner,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'contact_name')"));
    }

    [Fact]
    public void Contacts_AreIsolatedByOrganization_ForAppRuntime_AndCanBeRemoved()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var (orgA, customerA) = SeedCustomer(owner, "A");
        var (orgB, customerB) = SeedCustomer(owner, "B");
        Exec(owner, "INSERT INTO customer_contacts (id, organization_id, customer_id, first_name) VALUES ($1, $2, $3, 'Ana'), ($4, $5, $6, 'Beto')",
            Guid.NewGuid(), orgA, customerA, Guid.NewGuid(), orgB, customerB);

        using var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();

        using (var tx = app.BeginTransaction())
        {
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM customer_contacts")); // no scope: fail closed
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM customer_contacts"));
            Assert.Equal("Ana", Scalar<string>(app, "SELECT first_name FROM customer_contacts"));
            var crossOrg = Assert.Throws<PostgresException>(() => Exec(app,
                "INSERT INTO customer_contacts (id, organization_id, customer_id, first_name) VALUES ($1, $2, $3, 'Robado')", Guid.NewGuid(), orgB, customerB));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossOrg.SqlState);
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            Exec(app, "DELETE FROM customer_contacts WHERE true"); // replace-set semantics need DELETE
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM customer_contacts"));
            tx.Rollback();
        }
    }

    [Fact]
    public void Contact_RequiresAFirstName_ACustomerOfTheSameOrganization_AndAtMostOnePrimary()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var (orgA, customerA) = SeedCustomer(owner, "A");
        var (_, customerB) = SeedCustomer(owner, "B");
        const string insert =
            "INSERT INTO customer_contacts (id, organization_id, customer_id, first_name, last_name, is_primary) VALUES ($1, $2, $3, $4, 'X', $5)";

        var blank = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, customerA, "  ", false));
        Assert.Equal(PostgresErrorCodes.CheckViolation, blank.SqlState);

        // The customer must belong to the same organization (composite foreign key).
        var foreign = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, customerB, "Ana", false));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);

        Exec(owner, insert, Guid.NewGuid(), orgA, customerA, "Ana", true);
        Exec(owner, insert, Guid.NewGuid(), orgA, customerA, "Beto", false);
        Exec(owner, insert, Guid.NewGuid(), orgA, customerA, "Carla", false);
        var secondPrimary = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, customerA, "Dora", true));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, secondPrimary.SqlState);

        // Another customer can have its own primary.
        var (orgC, customerC) = SeedCustomer(owner, "C");
        Exec(owner, insert, Guid.NewGuid(), orgC, customerC, "Eva", true);
    }

    [Fact]
    public void Migration_MovesContactNameIntoAPrimaryContact_AndDropsTheColumn()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            ApplyAllMigrations(conn, f => string.CompareOrdinal(f, MigrationFile) < 0); // the world before 0029

            var org = Guid.NewGuid();
            Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, 'A')", org);
            var (withContact, blankContact, noContact) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            foreach (var (id, name, contact) in new (Guid, string, string?)[]
            {
                (withContact, "Con contacto", "  Lucas Badano "), (blankContact, "Contacto en blanco", "   "), (noContact, "Sin contacto", null),
            })
            {
                using var cmd = new NpgsqlCommand(
                    "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, contact_name) VALUES ($1, $2, 'Retail', $3, $4, $5)", conn);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(org);
                cmd.Parameters.AddWithValue(name);
                cmd.Parameters.AddWithValue(Guid.NewGuid());
                cmd.Parameters.AddWithValue((object?)contact ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            PostgresTestFixture.ApplyMigration(conn, MigrationFile); // idempotent: no duplicates, no error

            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM customer_contacts"));
            Assert.Equal("Lucas Badano", Scalar<string>(conn,
                "SELECT first_name FROM customer_contacts WHERE customer_id = $1 AND is_primary AND last_name IS NULL AND organization_id = $2", withContact, org));
            Assert.False(Scalar<bool>(conn,
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'contact_name')"));
        });
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheContactsMigrationVerbatim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var initPath = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql");
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(MigrationsDir, MigrationFile))), Lf(File.ReadAllText(initPath)));

        WithScratchDatabase(conn =>
        {
            var sql = File.ReadAllText(initPath)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using (var cmd = new NpgsqlCommand(sql, conn)) cmd.ExecuteNonQuery();

            Assert.True(Scalar<bool>(conn, "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = 'customer_contacts'"));
        });
    }
}
