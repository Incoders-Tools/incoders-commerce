using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0053_customer_location_reference.sql` (owner-requests-quick-adjustments Q1): the customer's location reference,
/// apart from its delivery notes.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerLocationReferenceMigrationTests
{
    private const string Migration = "0053_customer_location_reference.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);

    [Fact]
    public void Migration_AddsANullableColumn_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        PostgresTestFixture.ApplyMigration(owner, Migration);
        PostgresTestFixture.ApplyMigration(owner, Migration);

        using var column = new NpgsqlCommand(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'location_reference'", owner);
        Assert.Equal("YES", column.ExecuteScalar());
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
