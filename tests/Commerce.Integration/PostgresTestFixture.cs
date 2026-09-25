using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `PostgresCloudInboxStoreTests` and `PoolerScopingTests` both truncate and
/// read the SAME shared `sync_inbox` table against the SAME live Postgres
/// instance. xUnit parallelizes across test classes/collections by default;
/// without this shared collection, one class's `TRUNCATE` (via
/// `PostgresTestFixture.ResetInbox`) can race another class's concurrent
/// reads/writes and produce non-deterministic failures unrelated to the
/// actual tenant-scoping behavior under test.
/// </summary>
[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection;

/// <summary>
/// Shared connectivity helper for the Unit 2 live-Postgres integration tests
/// (`PostgresCloudInboxStoreTests`, `PoolerScopingTests`). These tests target
/// `deploy/dev/compose.yaml` (direct Postgres on 5432, transaction-mode
/// PgBouncer on 6543) — see `deploy/README.md` for how to start it.
///
/// If neither target is reachable (e.g. Docker unavailable in a given CI
/// runner), tests using <see cref="RequirePostgresOrSkip"/> report clearly
/// via the test output and return without asserting pass/fail, rather than
/// silently reporting green with no real coverage or hard-failing a CI
/// runner that never had the infrastructure to begin with. When
/// `deploy/dev/compose.yaml` IS running (the expected local/CI path per
/// design.md), these tests exercise the real code path end-to-end.
/// </summary>
public static class PostgresTestFixture
{
    /// <summary>
    /// The ONE database every live-Postgres test in this assembly talks to.
    ///
    /// Deliberately NOT `commerce_dev`. Roughly three dozen fixtures in this
    /// project reset themselves with `TRUNCATE TABLE ... users, branches,
    /// organizations CASCADE`, which is correct for a throwaway test database
    /// and catastrophic for the database a developer is running the app
    /// against: every `dotnet test` wiped the local sign-in accounts and they
    /// had to be re-provisioned by hand.
    ///
    /// `commerce_test` is created and given the same `deploy/dev/db/init-rls.sql`
    /// policy set by `deploy/dev/db/init-test-db.sql` — mounted into the
    /// Postgres container's init directory by `deploy/dev/compose.yaml`, and
    /// applied explicitly by the `build` job in `.github/workflows/release.yml`.
    /// Postgres ROLES (`app_runtime`, `platform_readonly`) are cluster-wide, so
    /// they already exist; the GRANTs and RLS policies are per-database, which
    /// is exactly why that file has to run against `commerce_test` too.
    ///
    /// Every connection string in the test suite is derived from this one
    /// constant, and `TestDatabaseIsolationTests` fails the build if any test
    /// file hardcodes `commerce_dev` again.
    /// </summary>
    public const string Database = "commerce_test";

    public const string DirectConnectionString =
        "Host=localhost;Port=5432;Database=" + Database + ";Username=app_runtime;Password=dev-only-password;Timeout=3";

    public const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=" + Database + ";Username=commerce_owner;Password=dev-only-password;Timeout=3";

    /// <summary>
    /// Transaction-mode PgBouncer on 6543. `deploy/dev/compose.yaml` no longer
    /// pins `DATABASES_DBNAME`, so its wildcard `[databases]` entry forwards
    /// whichever database the client asks for — which is what lets the pooler
    /// tests follow the suite onto <see cref="Database"/> while the dev app
    /// keeps reaching `commerce_dev` through the same port.
    /// </summary>
    public const string PooledConnectionString =
        "Host=localhost;Port=6543;Database=" + Database + ";Username=app_runtime;Password=dev-only-password;Timeout=3;" +
        "Pooling=false;No Reset On Close=true";

    public static bool TryPing(string connectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", connection);
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The repository root, found by walking up from the test binary until
    /// `Commerce.sln` appears. Lived as a private copy in every live-Postgres
    /// fixture; there is exactly one reason it could ever change, so there is
    /// one copy of it.
    /// </summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (Commerce.sln) from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Applies one repo migration on an open OWNER connection, substituting the
    /// dev password placeholders the shipped files carry.
    /// </summary>
    public static void ApplyMigration(NpgsqlConnection owner, string file)
    {
        var sql = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "db", "migrations", file))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
            .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        using var cmd = new NpgsqlCommand(sql, owner);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The exact migration chain and reset the three rate-component fixtures
    /// (`RateComponentStoreTests`, `PricingCompositionTests`,
    /// `PricingChannelParityTests`) each carried a byte-identical copy of —
    /// which is why adding `0013` meant editing all three, and `0014` would
    /// have meant editing them again.
    ///
    /// Deliberately NOT a general migration framework, and deliberately not
    /// shared with `MigrationRlsTests`, whose whole purpose is applying
    /// migrations in hand-picked partial chains. This is one named chain, used
    /// by the fixtures that genuinely need that one chain.
    ///
    /// `0012` is absent on purpose, exactly as in `MigrationRlsTests`: it only
    /// merges `platform_admins` into `users` and is orthogonal to pricing.
    /// </summary>
    public static void ApplyPricingMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(OwnerConnectionString);
        owner.Open();

        foreach (var file in new[]
                 {
                     "0001_init_rls.sql",
                     "0002_users.sql",
                     "0003_organizations_branches.sql",
                     "0009_catalog_and_pricing.sql",
                     "0013_rate_components.sql",
                     "0014_rate_component_tenancy.sql",
                 })
        {
            ApplyMigration(owner, file);
        }

        using var resetCmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE rate_components, rate_component_sets, price_list_entries, price_lists,
                           presentations, products, branches, organizations CASCADE
            """, owner);
        resetCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Truncates the shared `sync_inbox` table between tests. Uses the owner
    /// connection since app_runtime only has SELECT/INSERT/UPDATE, not
    /// TRUNCATE/DELETE, matching the RLS policy's intentionally narrow grant.
    /// </summary>
    public static void ResetInbox()
    {
        using var connection = new NpgsqlConnection(OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE sync_inbox", connection);
        cmd.ExecuteNonQuery();
    }
}
