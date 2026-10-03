using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0040_customer_party_type.sql` (admin-console-field-fixes T3b): every customer is a `Person` or a `Company`.
/// Existing customers are backfilled as Company when their tax id type is CUIT, otherwise Person. Re-runnable and
/// mirrored verbatim in the dev init snapshot.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerPartyTypeMigrationTests : IDisposable
{
    private const string Migration = "0040_customer_party_type.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public void Dispose()
    {
        if (!_postgresAvailable) return;
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        Exec(owner, "DELETE FROM organizations WHERE name LIKE 'cpt %'");
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

    private static string MigrationBody() =>
        string.Join('\n', File.ReadAllLines(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations", Migration))
            .Where(line => line.Trim() is not ("BEGIN;" or "COMMIT;")));

    private static Guid NewOrganization(NpgsqlConnection owner)
    {
        var org = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "cpt " + org);
        return org;
    }

    private static Guid NewCustomer(NpgsqlConnection owner, Guid org, string taxIdType, string? taxId)
    {
        var id = Guid.NewGuid();
        Exec(owner,
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, tax_id_type, tax_id) VALUES ($1, $2, 'Retail', 'Cliente', $3, $4, $5)",
            id, org, Guid.NewGuid(), taxIdType, (object?)taxId ?? DBNull.Value);
        return id;
    }

    [Fact]
    public void Migration_AddsANotNullPartyType_RestrictedToPersonOrCompany_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        PostgresTestFixture.ApplyMigration(owner, Migration);

        Assert.Equal("NO", Scalar<string>(owner, "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'party_type'"));
        var org = NewOrganization(owner);
        var customer = NewCustomer(owner, org, "None", null);
        Assert.Equal("Person", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", customer));

        Exec(owner, "UPDATE customers SET party_type = 'Company' WHERE id = $1", customer);
        var bad = Assert.Throws<PostgresException>(() => Exec(owner, "UPDATE customers SET party_type = 'Empresa' WHERE id = $1", customer));
        Assert.Equal(PostgresErrorCodes.CheckViolation, bad.SqlState);
    }

    [Fact]
    public void Migration_BackfillsCompanyForACuit_AndPersonOtherwise()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        using var tx = owner.BeginTransaction();
        Exec(owner, "ALTER TABLE customers DROP COLUMN party_type");
        var org = NewOrganization(owner);
        var cuit = NewCustomer(owner, org, "Cuit", "30123456789");
        var dni = NewCustomer(owner, org, "Dni", "12345678");
        var cuil = NewCustomer(owner, org, "Cuil", "20123456789");
        var none = NewCustomer(owner, org, "None", null);

        Exec(owner, MigrationBody());

        Assert.Equal("Company", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", cuit));
        Assert.Equal("Person", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", dni));
        Assert.Equal("Person", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", cuil));
        Assert.Equal("Person", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", none));
        tx.Rollback();
    }

    [Fact]
    public void Rerunning_TheMigration_NeverOverwritesAnEditedPartyType()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwnerWithMigrations();
        var org = NewOrganization(owner);
        var cuit = NewCustomer(owner, org, "Cuit", "30123456789");
        Exec(owner, "UPDATE customers SET party_type = 'Person' WHERE id = $1", cuit);

        PostgresTestFixture.ApplyMigration(owner, Migration);

        Assert.Equal("Person", Scalar<string>(owner, "SELECT party_type FROM customers WHERE id = $1", cuit));
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
