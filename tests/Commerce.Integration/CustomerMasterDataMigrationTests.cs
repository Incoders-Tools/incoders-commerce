using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0027_customer_master_data.sql`: the organization-owned `business_types`
/// catalog, the optional customer references and the `Dni` tax id type. (Its
/// `contact_name` column was retired by 0029; see
/// <see cref="CustomerContactsMigrationTests"/>.) (Its organization `cities` table was retired by 0028; see
/// <see cref="CoreGeographyMigrationTests"/>.)
/// </summary>
[Collection("Postgres")]
public sealed class CustomerMasterDataMigrationTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
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
        return (T)cmd.ExecuteScalar()!;
    }

    private static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
    }

    [Theory]
    [InlineData("business_types")]
    public void Catalog_TableExists_WithForcedRowLevelSecurity(string table)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        ApplyAllMigrations(owner); // re-runnable

        Assert.True(Scalar<bool>(owner,
            "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = $1 AND relrowsecurity AND relforcerowsecurity)", table));
    }

    [Theory]
    [InlineData("business_types")]
    public void Catalog_IsIsolatedByOrganization_ForAppRuntime_AndHasNoDeleteGrant(string table)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        using (var owner = OpenOwner())
        {
            ApplyAllMigrations(owner);
            Exec(owner, $"INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);
            Exec(owner, $"INSERT INTO {table} (id, organization_id, name, key) VALUES ($1, $2, 'Moreno', 'moreno'), ($3, $4, 'Moreno', 'moreno')",
                Guid.NewGuid(), orgA, Guid.NewGuid(), orgB);
        }

        using var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            Assert.Equal(1L, Scalar<long>(app, $"SELECT count(*) FROM {table} WHERE key = 'moreno'"));
            var crossOrg = Assert.Throws<PostgresException>(() => Exec(app,
                $"INSERT INTO {table} (id, organization_id, name, key) VALUES ($1, $2, 'Robada', 'robada')", Guid.NewGuid(), orgB));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossOrg.SqlState);
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Assert.Equal(0L, Scalar<long>(app, $"SELECT count(*) FROM {table}")); // no scope: fail closed
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            var delete = Assert.Throws<PostgresException>(() => Exec(app, $"DELETE FROM {table} WHERE true"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
            tx.Rollback();
        }
    }

    [Theory]
    [InlineData("business_types")]
    public void Catalog_EnforcesUniqueNameAndKeyPerOrganization(string table)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);

        var insert = $"INSERT INTO {table} (id, organization_id, name, key) VALUES ($1, $2, $3, $4)";
        Exec(owner, insert, Guid.NewGuid(), orgA, "San Miguel", "san_miguel");
        Exec(owner, insert, Guid.NewGuid(), orgB, "San Miguel", "san_miguel");

        var sameName = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, " san miguel ", "otra"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, sameName.SqlState);
        var sameKey = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, "Otro", "san_miguel"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, sameKey.SqlState);
        var blank = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, "  ", "blank"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, blank.SqlState);
    }

    [Fact]
    public void Customer_RefusesAnUnknownCityOrABusinessTypeOfAnotherOrganization_AndBlocksDeletingOneInUse()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
        var (cityA, typeA, typeB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);
        Exec(owner, "INSERT INTO cities (id, name, province_id) VALUES ($1, 'Moreno de Prueba ' || $2::text, '06')", cityA, cityA.ToString());
        Exec(owner, "INSERT INTO business_types (id, organization_id, name, key) VALUES ($1, $2, 'Bar', 'bar'), ($3, $4, 'Bar', 'bar')", typeA, orgA, typeB, orgB);

        const string insert =
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, city_id, business_type_id) VALUES ($1, $2, 'Retail', 'Cliente', $3, $4, $5)";

        var unknownCity = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, Guid.NewGuid(), Guid.NewGuid(), typeA));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownCity.SqlState);
        var foreignType = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, Guid.NewGuid(), cityA, typeB));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignType.SqlState);

        Exec(owner, insert, Guid.NewGuid(), orgA, Guid.NewGuid(), cityA, typeA);
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND city_id = $2", orgA, cityA));

        var cityInUse = Assert.Throws<PostgresException>(() => Exec(owner, "DELETE FROM cities WHERE id = $1", cityA));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, cityInUse.SqlState);
        var typeInUse = Assert.Throws<PostgresException>(() => Exec(owner, "DELETE FROM business_types WHERE id = $1", typeA));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, typeInUse.SqlState);
    }

    [Fact]
    public void Customer_AcceptsDniTaxIdType_AndStillRejectsUnknownOnes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var orgA = Guid.NewGuid();
        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'A')", orgA);

        const string insert =
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, tax_id_type, tax_id) VALUES ($1, $2, 'Retail', 'Cliente', $3, $4, $5)";
        Exec(owner, insert, Guid.NewGuid(), orgA, Guid.NewGuid(), "Dni", "12345678");

        var unknown = Assert.Throws<PostgresException>(() => Exec(owner, insert, Guid.NewGuid(), orgA, Guid.NewGuid(), "Passport", "X1"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unknown.SqlState);
        var dniWithoutId = Assert.Throws<PostgresException>(() => Exec(owner,
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, tax_id_type) VALUES ($1, $2, 'Retail', 'X', $3, 'Dni')",
            Guid.NewGuid(), orgA, Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.CheckViolation, dniWithoutId.SqlState);
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheMasterDataTablesAndColumns()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var dbName = "md0027_" + Guid.NewGuid().ToString("N");
        using (var admin = OpenOwner())
        {
            Exec(admin, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        }
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;
            using var conn = new NpgsqlConnection(cs);
            conn.Open();
            var sql = File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql"))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using (var cmd = new NpgsqlCommand(sql, conn)) { cmd.ExecuteNonQuery(); }

            Assert.True(Scalar<bool>(conn, "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'cities' AND relrowsecurity AND relforcerowsecurity)"));
            Assert.True(Scalar<bool>(conn, "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'business_types' AND relrowsecurity AND relforcerowsecurity)"));
            Assert.True(Scalar<bool>(conn, "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'customer_contacts' AND relrowsecurity AND relforcerowsecurity)"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = OpenOwner();
            Exec(admin, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }
}
