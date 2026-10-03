using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0039_organization_country_and_city_postal_code.sql` (admin-console-field-fixes T2): every organization has a
/// country (`countries.code`, default and backfill Argentina) and a city may carry an optional postal code (Argentine
/// CP "2000" or CPA "S2000ABC"). Re-runnable and mirrored verbatim in the dev init snapshot.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationCountryAndCityPostalCodeMigrationTests : IDisposable
{
    private const string Migration = "0039_organization_country_and_city_postal_code.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public void Dispose()
    {
        if (!_postgresAvailable) return;
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        Exec(owner, "DELETE FROM organizations WHERE name LIKE 'ocp %'");
        Exec(owner, "DELETE FROM cities WHERE name LIKE 'ocp %'");
    }

    private static NpgsqlConnection OpenOwnerWithMigrations()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

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

    /// <summary>The migration body without its own BEGIN/COMMIT, so a test can run it inside a transaction it rolls back.</summary>
    private static string MigrationBody() =>
        string.Join('\n', File.ReadAllLines(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations", Migration))
            .Where(line => line.Trim() is not ("BEGIN;" or "COMMIT;")));

    [Fact]
    public void Migration_AddsTheCountryNotNullDefaultingToArgentina_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        PostgresTestFixture.ApplyMigration(owner, Migration);

        Assert.Equal("NO", Scalar<string>(owner, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'organizations' AND column_name = 'country_code'"));
        var org = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "ocp " + org);
        Assert.Equal("AR", Scalar<string>(owner, "SELECT country_code FROM organizations WHERE id = $1", org));

        var unknown = Assert.Throws<PostgresException>(() => Exec(owner, "UPDATE organizations SET country_code = 'QQ' WHERE id = $1", org));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknown.SqlState);
        Assert.Equal("organizations_country_fk", unknown.ConstraintName);
    }

    [Fact]
    public void Migration_BackfillsExistingOrganizationsWithArgentina()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        using var tx = owner.BeginTransaction();
        Exec(owner, "ALTER TABLE organizations DROP COLUMN country_code");
        var org = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "ocp " + org);

        Exec(owner, MigrationBody());

        Assert.Equal("AR", Scalar<string>(owner, "SELECT country_code FROM organizations WHERE id = $1", org));
        tx.Rollback();
    }

    [Fact]
    public void CityPostalCode_IsOptional_AndOnlyAcceptsACpOrACpa()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        var city = Guid.NewGuid();
        Exec(owner, "INSERT INTO cities (id, name, province_id) VALUES ($1, $2, '82')", city, "ocp " + city);
        Assert.True(Scalar<bool>(owner, "SELECT postal_code IS NULL FROM cities WHERE id = $1", city));

        Exec(owner, "UPDATE cities SET postal_code = '2000' WHERE id = $1", city);
        Exec(owner, "UPDATE cities SET postal_code = 'S2000ABC' WHERE id = $1", city);
        Assert.Equal("S2000ABC", Scalar<string>(owner, "SELECT postal_code FROM cities WHERE id = $1", city));

        foreach (var bad in new[] { "200", "s2000abc", "S2000AB", "ABCD" })
        {
            var ex = Assert.Throws<PostgresException>(() => Exec(owner, "UPDATE cities SET postal_code = $1 WHERE id = $2", bad, city));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", Migration))), init);
    }
}
