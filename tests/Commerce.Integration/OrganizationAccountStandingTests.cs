using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0052_organization_account_standing.sql` (odd/tasks/organization-account-standing.md T2): the inputs of
/// <c>AccountStandingRules.Evaluate</c> stored on the organization: billing due date, grace days and manual suspension.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationAccountStandingTests
{
    private const string Migration = "0052_organization_account_standing.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);

    public OrganizationAccountStandingTests()
    {
        if (_postgresAvailable) ApplyMigrations();
    }

    private static void ApplyMigrations()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        foreach (var file in Directory.GetFiles(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations"), "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            PostgresTestFixture.ApplyMigration(owner, file);
    }

    private static Guid InsertOrganization(NpgsqlConnection owner)
    {
        var id = Guid.NewGuid();
        using var insert = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Standing ' || $1::text)", owner);
        insert.Parameters.AddWithValue(id);
        insert.ExecuteNonQuery();
        return id;
    }

    private static void SetGraceDays(NpgsqlConnection owner, Guid id, int graceDays)
    {
        using var update = new NpgsqlCommand("UPDATE organizations SET billing_grace_days = $2 WHERE id = $1", owner);
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(graceDays);
        update.ExecuteNonQuery();
    }

    [Fact]
    public void Migration_LeavesEveryOrganizationUntracked_WithThirtyGraceDays_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        PostgresTestFixture.ApplyMigration(owner, Migration);
        PostgresTestFixture.ApplyMigration(owner, Migration);

        var id = InsertOrganization(owner);
        using var read = new NpgsqlCommand("SELECT billing_due_on, billing_grace_days, suspended_at FROM organizations WHERE id = $1", owner);
        read.Parameters.AddWithValue(id);
        using var row = read.ExecuteReader();
        Assert.True(row.Read());
        Assert.True(row.IsDBNull(0));
        Assert.Equal(30, row.GetInt32(1));
        Assert.True(row.IsDBNull(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void GraceDays_AcceptZeroToNinety(int graceDays)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var id = InsertOrganization(owner);

        SetGraceDays(owner, id, graceDays);

        using var read = new NpgsqlCommand("SELECT billing_grace_days FROM organizations WHERE id = $1", owner);
        read.Parameters.AddWithValue(id);
        Assert.Equal(graceDays, (int)read.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void GraceDays_RejectOutsideZeroToNinety(int graceDays)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var id = InsertOrganization(owner);

        var error = Assert.Throws<PostgresException>(() => SetGraceDays(owner, id, graceDays));
        Assert.Equal("organizations_billing_grace_days_ck", error.ConstraintName);
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheAccountStandingMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", Migration))), init);
    }
}
