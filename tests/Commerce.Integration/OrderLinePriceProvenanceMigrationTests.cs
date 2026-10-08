using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0038_order_line_price_provenance.sql` (customer-price-lists T6): order lines record which price list priced them and
/// whether the default list priced them as a fallback. Additive, re-runnable, mirrored verbatim in the dev init snapshot.
/// </summary>
[Collection("Postgres")]
public sealed class OrderLinePriceProvenanceMigrationTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    [Fact]
    public void Migration_AddsTheProvenanceColumns_NullableAndDefaultingToNotFellBack_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        PostgresTestFixture.ApplyMigration(owner, "0038_order_line_price_provenance.sql");

        using var cmd = new NpgsqlCommand(
            """
            SELECT column_name, is_nullable, column_default FROM information_schema.columns
            WHERE table_name = 'order_lines' AND column_name IN ('priced_from_price_list_id', 'price_fell_back')
            ORDER BY column_name
            """, owner);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("price_fell_back", reader.GetString(0));
        Assert.Equal("NO", reader.GetString(1));
        Assert.Equal("false", reader.GetString(2));
        Assert.True(reader.Read());
        Assert.Equal("priced_from_price_list_id", reader.GetString(0));
        Assert.Equal("YES", reader.GetString(1));
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheProvenanceMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0038_order_line_price_provenance.sql"))), init);
    }
}
