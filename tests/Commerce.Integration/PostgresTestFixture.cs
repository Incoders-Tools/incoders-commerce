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
    public const string DirectConnectionString =
        "Host=localhost;Port=5432;Database=commerce_dev;Username=app_runtime;Password=dev-only-password;Timeout=3";

    public const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=commerce_dev;Username=commerce_owner;Password=dev-only-password;Timeout=3";

    public const string PooledConnectionString =
        "Host=localhost;Port=6543;Database=commerce_dev;Username=app_runtime;Password=dev-only-password;Timeout=3;" +
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
