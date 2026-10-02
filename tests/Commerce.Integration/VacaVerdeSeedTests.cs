using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `deploy/db/seeds/vaca-verde/001_vaca_verde_master_data.sql`: the versioned,
/// idempotent seed with Vaca Verde's real business types and customers (cities
/// are global Georef data since 0028; the seed only references them by INDEC id
/// and restores the owner's original audit dates). It resolves the organization by name and the creating user from that
/// organization's business admin, and does nothing when either is missing.
/// </summary>
[Collection("Postgres")]
public sealed class VacaVerdeSeedTests
{
    private const int ExpectedBusinessTypes = 11;
    private const int ExpectedCustomers = 87;

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string SeedSql() =>
        File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "seeds", "vaca-verde", "001_vaca_verde_master_data.sql"));

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
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

    private static void ApplySeed(NpgsqlConnection owner) => Exec(owner, SeedSql());

    private static void RemoveVacaVerde(NpgsqlConnection owner)
    {
        Exec(owner, "DELETE FROM customers WHERE organization_id IN (SELECT id FROM organizations WHERE lower(name) = 'vaca verde')");
        Exec(owner, "DELETE FROM users WHERE organization_id IN (SELECT id FROM organizations WHERE lower(name) = 'vaca verde')");
        Exec(owner, "DELETE FROM organizations WHERE lower(name) = 'vaca verde'"); // cascades business_types
    }

    private static Guid ProvisionVacaVerde(NpgsqlConnection owner, bool withBusinessAdmin = true)
    {
        RemoveVacaVerde(owner);
        var orgId = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Vaca Verde')", orgId);
        if (withBusinessAdmin)
        {
            Exec(owner,
                "INSERT INTO users (id, organization_id, email, password_hash, roles) VALUES ($1, $2, $3, 'x', '[{\"name\":\"business-admin\",\"permissions\":0}]'::jsonb)",
                Guid.NewGuid(), orgId, $"admin-{orgId:N}@vacaverde.test");
        }
        return orgId;
    }

    private static long Count(NpgsqlConnection owner, string table, Guid orgId) =>
        Scalar<long>(owner, $"SELECT count(*) FROM {table} WHERE organization_id = $1", orgId);

    [Fact]
    public void Seed_LoadsBusinessTypesAndCustomers_ForTheVacaVerdeOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgId = ProvisionVacaVerde(owner);
        try
        {
            ApplySeed(owner);

            Assert.Equal(ExpectedBusinessTypes, Count(owner, "business_types", orgId));
            Assert.Equal(ExpectedCustomers, Count(owner, "customers", orgId));

            // Every customer is created by the organization's business admin.
            var adminId = Scalar<Guid>(owner, "SELECT id FROM users WHERE organization_id = $1", orgId);
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND created_by_user_id <> $2", orgId, adminId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND (customer_kind <> 'Wholesale' OR NOT is_enabled)", orgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void Seed_DoesNotCreateCities_AndRestoresTheOwnersAuditDatesOnlyOnUntouchedGlobalRows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgId = ProvisionVacaVerde(owner);
        try
        {
            // Model a fresh environment: the Georef rows were just loaded (created = updated = now).
            Exec(owner, "UPDATE cities SET created_at_utc = now(), updated_at_utc = now() WHERE indec_id IN ('06140010', '06077010')");
            Exec(owner, "UPDATE cities SET updated_at_utc = now() + interval '1 second' WHERE indec_id = '06077010'"); // edited by someone since
            var citiesBefore = Scalar<long>(owner, "SELECT count(*) FROM cities");

            ApplySeed(owner);

            Assert.Equal(citiesBefore, Scalar<long>(owner, "SELECT count(*) FROM cities")); // no city is created or renamed
            using var cmd = new NpgsqlCommand(
                "SELECT name, created_at_utc, updated_at_utc FROM cities WHERE indec_id = '06140010'", owner);
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("Capitán Sarmiento", r.GetString(0));
            Assert.Equal(DateTimeOffset.Parse("2026-01-10T04:30:02.811173Z"), r.GetFieldValue<DateTimeOffset>(1));
            Assert.Equal(DateTimeOffset.Parse("2026-01-10T04:30:02.917Z"), r.GetFieldValue<DateTimeOffset>(2));
            r.Close();

            // A row edited after loading keeps its dates (the seed never overwrites later edits).
            Assert.True(Scalar<DateTime>(owner, "SELECT created_at_utc FROM cities WHERE indec_id = '06077010'") > DateTime.UtcNow.AddHours(-1));
            // The organization owns no city table any more.
            Assert.Equal(0L, Scalar<long>(owner,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'cities' AND column_name = 'organization_id'"));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void Seed_LinksCustomersToTheirCityAndBusinessType_AndKeepsTaxIdsAndNotes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgId = ProvisionVacaVerde(owner);
        try
        {
            ApplySeed(owner);

            Assert.True(Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND city_id IS NOT NULL", orgId) > 50);
            Assert.True(Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND business_type_id IS NOT NULL", orgId) > 50);
            Assert.True(Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND tax_id_type = 'Dni'", orgId) > 0);
            Assert.True(Scalar<long>(owner, "SELECT count(*) FROM customers WHERE organization_id = $1 AND tax_id_type = 'Cuit'", orgId) > 0);
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM customers c JOIN cities ci ON ci.id = c.city_id WHERE c.organization_id = $1 AND c.display_name = 'Almacén Cristian' AND ci.indec_id = '06077010' AND ci.name = 'Arrecifes'", orgId));
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM customers c JOIN cities ci ON ci.id = c.city_id WHERE c.organization_id = $1 AND c.display_name = 'Al Toque' AND ci.indec_id = '06140010'", orgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void Seed_IsIdempotent_RunningTwiceLeavesTheSameData_AndPreservesLaterEdits()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgId = ProvisionVacaVerde(owner);
        try
        {
            ApplySeed(owner);
            Exec(owner, "UPDATE customers SET notes = 'editado por el dueno' WHERE organization_id = $1 AND display_name = 'Almacén Cristian'", orgId);
            ApplySeed(owner);

            Assert.Equal(ExpectedBusinessTypes, Count(owner, "business_types", orgId));
            Assert.Equal(ExpectedCustomers, Count(owner, "customers", orgId));
            Assert.Equal("editado por el dueno", Scalar<string>(owner,
                "SELECT notes FROM customers WHERE organization_id = $1 AND display_name = 'Almacén Cristian'", orgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void Seed_IsANoOp_WhenTheOrganizationDoesNotExist()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        RemoveVacaVerde(owner);
        var (cities, types, customers) = (
            Scalar<string>(owner, "SELECT md5(string_agg(id::text || created_at_utc::text || updated_at_utc::text, ',' ORDER BY id)) FROM cities"),
            Scalar<long>(owner, "SELECT count(*) FROM business_types"),
            Scalar<long>(owner, "SELECT count(*) FROM customers"));

        ApplySeed(owner); // must not throw

        Assert.Equal(cities, Scalar<string>(owner, "SELECT md5(string_agg(id::text || created_at_utc::text || updated_at_utc::text, ',' ORDER BY id)) FROM cities"));
        Assert.Equal(types, Scalar<long>(owner, "SELECT count(*) FROM business_types"));
        Assert.Equal(customers, Scalar<long>(owner, "SELECT count(*) FROM customers"));
    }

    [Fact]
    public void Seed_IsANoOp_WhenTheOrganizationHasNoBusinessAdmin()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgId = ProvisionVacaVerde(owner, withBusinessAdmin: false);
        try
        {
            ApplySeed(owner); // must not throw

            Assert.Equal(0L, Count(owner, "business_types", orgId));
            Assert.Equal(0L, Count(owner, "customers", orgId));
        }
        finally { RemoveVacaVerde(owner); }
    }
}
