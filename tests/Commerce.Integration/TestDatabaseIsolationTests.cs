using System.IO;

namespace Commerce.Integration;

/// <summary>
/// The regression guard for the reason `PostgresTestFixture.Database` exists.
///
/// Roughly three dozen fixtures in this assembly reset themselves with
/// `TRUNCATE TABLE ... users, branches, organizations CASCADE`. While the
/// suite pointed at `commerce_dev`, every `dotnet test` destroyed the accounts
/// a developer had provisioned for the running app, and they had to be
/// re-created by hand.
///
/// Renaming the database in one place fixes that once; nothing stops the next
/// fixture from pasting a literal connection string back in. These tests are
/// what makes the isolation structural instead of conventional — they parse
/// the test sources themselves, need no database, and fail the build the
/// moment a `commerce_dev` literal reappears anywhere under
/// `tests/Commerce.Integration`.
/// </summary>
public sealed class TestDatabaseIsolationTests
{
    /// <summary>
    /// Kept as a separate constant rather than spelled inline so this file
    /// does not match its own scan below.
    /// </summary>
    private const string DeveloperDatabase = "commerce_dev";

    private static DirectoryInfo ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Commerce.sln) from " + AppContext.BaseDirectory);
        }

        return dir;
    }

    [Fact]
    public void Fixture_TargetsADedicatedTestDatabase_NeverTheDeveloperDatabase()
    {
        Assert.Equal("commerce_test", PostgresTestFixture.Database);

        foreach (var connectionString in new[]
                 {
                     PostgresTestFixture.DirectConnectionString,
                     PostgresTestFixture.OwnerConnectionString,
                     PostgresTestFixture.PooledConnectionString,
                     MigrationRlsTests.PlatformReadonlyConnectionString,
                 })
        {
            Assert.Contains("Database=commerce_test;", connectionString);
            Assert.DoesNotContain("commerce_dev", connectionString);
        }
    }

    /// <summary>
    /// The pooled string must stay on 6543 while naming the test database:
    /// that combination is only reachable because `deploy/dev/compose.yaml`
    /// dropped PgBouncer's `DATABASES_DBNAME` pin, letting its wildcard
    /// `[databases]` entry forward the client-requested database through.
    /// </summary>
    [Fact]
    public void PooledConnectionString_StillGoesThroughTheTransactionPooler()
    {
        Assert.Contains("Port=6543;", PostgresTestFixture.PooledConnectionString);
        Assert.Contains("Database=commerce_test;", PostgresTestFixture.PooledConnectionString);
    }

    /// <summary>
    /// Scans for the connection-string form specifically (`Database=` followed
    /// by the developer database) rather than any mention of the name, so that
    /// prose explaining WHY the suite moved off it — the comments in
    /// <see cref="PostgresTestFixture"/> and in this file — stays allowed while
    /// an actual connection to it does not.
    /// </summary>
    [Fact]
    public void NoIntegrationTestSource_ConnectsToTheDeveloperDatabase()
    {
        var needle = "Database=" + DeveloperDatabase;
        var testDirectory = Path.Combine(ResolveRepoRoot().FullName, "tests", "Commerce.Integration");
        var offenders = Directory
            .EnumerateFiles(testDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These test files connect to `{DeveloperDatabase}` — the database the developer's own app runs against, " +
            "which this suite truncates. Derive the connection string from `PostgresTestFixture.Database` instead: " +
            string.Join(", ", offenders));
    }
}
