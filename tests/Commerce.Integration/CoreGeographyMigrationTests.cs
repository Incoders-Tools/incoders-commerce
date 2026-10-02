using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0028_core_geography.sql`: global `countries` / `provinces` / `cities`
/// loaded from Georef, customers pointing at a global city, and the retirement
/// of the organization-scoped `cities` of 0027 with its data mapped.
/// </summary>
[Collection("Postgres")]
public sealed class CoreGeographyMigrationTests
{
    private const string MigrationFile = "0028_core_geography.sql";

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

    /// <summary>Runs <paramref name="body"/> against a throwaway database, dropped afterwards.</summary>
    private static void WithScratchDatabase(Action<NpgsqlConnection> body)
    {
        var dbName = "geo0028_" + Guid.NewGuid().ToString("N");
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

    [Fact]
    public void Migration_LoadsGeorefProvincesAndLocalities_AndIsReRunnable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        ApplyAllMigrations(owner); // re-runnable, including 0027 after the retirement

        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM countries WHERE code = 'AR' AND iso3 = 'ARG'"));
        Assert.Equal(24L, Scalar<long>(owner, "SELECT count(*) FROM provinces WHERE country_code = 'AR'"));
        Assert.True(Scalar<long>(owner, "SELECT count(*) FROM cities WHERE indec_id IS NOT NULL") >= 4037);
        Assert.Equal("AR-B", Scalar<string>(owner, "SELECT iso_code FROM provinces WHERE id = '06'"));
        Assert.Equal("Capitán Sarmiento", Scalar<string>(owner, "SELECT name FROM cities WHERE indec_id = '06140010'"));
        Assert.Equal("06", Scalar<string>(owner, "SELECT province_id FROM cities WHERE indec_id = '06770040'")); // Río Tala
        Assert.Equal("02", Scalar<string>(owner, "SELECT province_id FROM cities WHERE indec_id = '02014010'")); // Ciudad de Buenos Aires
        Assert.Equal("capitan sarmiento", Scalar<string>(owner, "SELECT search_key FROM cities WHERE indec_id = '06140010'"));
        // The retired organization table and its foreign key are gone; the customer FK targets the global table.
        Assert.False(Scalar<bool>(owner, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'cities' AND column_name = 'organization_id')"));
        Assert.Null(Scalar<string?>(owner, "SELECT to_regclass('public.org_cities_retired')::text"));
        Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM pg_constraint WHERE conname = 'customers_city_org_fk'"));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM pg_constraint WHERE conname = 'customers_city_fk'"));
    }

    [Theory]
    [InlineData("countries")]
    [InlineData("provinces")]
    [InlineData("cities")]
    public void GeographyTable_HasForcedRowLevelSecurity_IsReadableByAnyTenant_AndHasNoDeleteGrant(string table)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = OpenOwner()) ApplyAllMigrations(owner);
        using var owner2 = OpenOwner();
        Assert.True(Scalar<bool>(owner2, "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = $1", table));

        using var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();
        using (var tx = app.BeginTransaction())
        {
            // No tenant scope at all: reference data is still readable.
            Assert.True(Scalar<long>(app, $"SELECT count(*) FROM {table}") > 0);
            tx.Rollback();
        }
        using (var tx = app.BeginTransaction())
        {
            var delete = Assert.Throws<PostgresException>(() => Exec(app, $"DELETE FROM {table} WHERE false"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
            tx.Rollback();
        }
    }

    [Fact]
    public void AppRuntime_CanAddAndEditCities_ButNotCountriesOrProvinces_AndManualDuplicatesAreRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using (var owner = OpenOwner()) ApplyAllMigrations(owner);
        var name = "Ciudad Test " + Guid.NewGuid().ToString("N")[..8];

        using var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();
        var id = Guid.NewGuid();
        Exec(app, "INSERT INTO cities (id, name, province_id, department_name) VALUES ($1, $2, '06', 'Salto')", id, name);
        Exec(app, "UPDATE cities SET is_active = false WHERE id = $1", id);

        var duplicate = Assert.Throws<PostgresException>(() => Exec(app,
            "INSERT INTO cities (id, name, province_id, department_name) VALUES ($1, $2, '06', ' salto ')", Guid.NewGuid(), name.ToUpperInvariant()));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        var unknownProvince = Assert.Throws<PostgresException>(() => Exec(app,
            "INSERT INTO cities (id, name, province_id) VALUES ($1, 'Nada', '99')", Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownProvince.SqlState);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.Throws<PostgresException>(() => Exec(app,
            "INSERT INTO provinces (id, country_code, iso_code, name) VALUES ('99', 'AR', 'AR-Z', 'Nueva')")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.Throws<PostgresException>(() => Exec(app,
            "UPDATE countries SET name = 'X'")).SqlState);
    }

    [Fact]
    public void Migration_MapsExistingOrganizationCities_RepointsCustomers_AndKeepsAuditDates()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            ApplyAllMigrations(conn, f => string.CompareOrdinal(f, MigrationFile) < 0); // the world before 0028

            var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
            Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, 'A'), ($2, 'B')", orgA, orgB);

            var created = DateTimeOffset.Parse("2026-01-10T04:30:02.811173Z");
            var updated = DateTimeOffset.Parse("2026-01-12T10:00:00Z");
            var cities = new Dictionary<string, Guid>();
            foreach (var (org, name) in new[]
            {
                (orgA, "Capitán Sarmiento"), (orgA, "Río Tala"), (orgA, "Capital Federal"), (orgA, "Arrecifes"),
                (orgA, "San Nicolás"), (orgA, "Urquiza"), (orgA, "Doyle"), (orgA, "Córdoba"), (orgB, "arrecifes"),
            })
            {
                var id = Guid.NewGuid();
                cities[org == orgB ? "B:" + name : name] = id;
                Exec(conn, "INSERT INTO cities (id, organization_id, name, key, created_at_utc, updated_at_utc) VALUES ($1, $2, $3, $4, $5, $6)",
                    id, org, name, name.ToLowerInvariant().Replace(' ', '_'), created, updated);
            }

            var customers = new Dictionary<string, Guid>();
            foreach (var (org, key) in new[]
            {
                (orgA, "Capitán Sarmiento"), (orgA, "Urquiza"), (orgA, "San Nicolás"), (orgB, "B:arrecifes"),
            })
            {
                var id = Guid.NewGuid();
                customers[key] = id;
                Exec(conn,
                    "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, city_id) VALUES ($1, $2, 'Retail', $3, $4, $5)",
                    id, org, "Cliente " + key, Guid.NewGuid(), cities[key]);
            }

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            string? CityIndec(Guid customerId) => Scalar<string?>(conn,
                "SELECT ci.indec_id FROM customers cu LEFT JOIN cities ci ON ci.id = cu.city_id WHERE cu.id = $1", customerId);

            Assert.Equal("06140010", CityIndec(customers["Capitán Sarmiento"]));
            Assert.Equal("06763050", CityIndec(customers["San Nicolás"]));          // prefix: San Nicolás de los Arroyos
            Assert.Null(CityIndec(customers["Urquiza"]));                            // ambiguous: left empty
            Assert.Equal("06077010", CityIndec(customers["B:arrecifes"]));          // same global city for every organization
            Assert.Equal(Scalar<Guid>(conn, "SELECT id FROM cities WHERE indec_id = '06077010'"),
                Scalar<Guid>(conn, "SELECT city_id FROM customers WHERE id = $1", customers["B:arrecifes"]));

            // Audit dates of the original rows survive on the global rows.
            Assert.Equal(created.UtcDateTime, Scalar<DateTime>(conn, "SELECT created_at_utc FROM cities WHERE indec_id = '06140010'"));
            Assert.Equal(updated.UtcDateTime, Scalar<DateTime>(conn, "SELECT updated_at_utc FROM cities WHERE indec_id = '06140010'"));
            // Capital Federal and Río Tala and Córdoba resolve as decided / by exact name.
            Assert.Equal(created.UtcDateTime, Scalar<DateTime>(conn, "SELECT created_at_utc FROM cities WHERE indec_id = '02014010'"));
            Assert.Equal(created.UtcDateTime, Scalar<DateTime>(conn, "SELECT created_at_utc FROM cities WHERE indec_id = '06770040'"));
            Assert.Equal(created.UtcDateTime, Scalar<DateTime>(conn, "SELECT created_at_utc FROM cities WHERE indec_id = '14014010'"));
            // A Georef row nobody used keeps the load time, not the owner dates.
            Assert.NotEqual(created.UtcDateTime, Scalar<DateTime>(conn, "SELECT created_at_utc FROM cities WHERE indec_id = '06441030'"));

            Assert.Null(Scalar<string?>(conn, "SELECT to_regclass('public.org_cities_retired')::text"));
        });
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheGeographyMigrationVerbatim_AndLoadsTheGeorefData()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var initPath = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql");
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var init = Lf(File.ReadAllText(initPath));
        var migration = Lf(File.ReadAllText(Path.Combine(MigrationsDir, MigrationFile)));
        Assert.Contains(migration, init); // hand-kept mirror convention: appended verbatim

        WithScratchDatabase(conn =>
        {
            var sql = File.ReadAllText(initPath)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using (var cmd = new NpgsqlCommand(sql, conn)) cmd.ExecuteNonQuery();

            Assert.Equal(24L, Scalar<long>(conn, "SELECT count(*) FROM provinces"));
            Assert.Equal(4037L, Scalar<long>(conn, "SELECT count(*) FROM cities"));
        });
    }
}
