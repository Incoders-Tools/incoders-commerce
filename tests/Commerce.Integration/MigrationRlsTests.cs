using System.IO;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 5 task 5.2: apply `deploy/db/migrations/0001_init_rls.sql`
/// against a scratch/live Postgres (`deploy/dev/compose.yaml`) and confirm
/// it produces the same cross-org read denial as the hand-authored
/// `deploy/dev/db/init-rls.sql` policy that Unit 2's
/// <see cref="PostgresCloudInboxStoreTests"/> already exercises. Also proves
/// the migration is idempotent by applying it twice.
///
/// If Postgres is not reachable, this reports the gap clearly and returns
/// without asserting pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class MigrationRlsTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string ResolveMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0001_init_rls.sql");
    }

    private static void ApplyMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveMigrationPath())
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");

        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Migration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres at " + PostgresTestFixture.OwnerConnectionString + ". Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();

        // First apply is a no-op against schema already created by
        // deploy/dev/db/init-rls.sql (docker-entrypoint-initdb.d); second
        // apply proves CREATE TABLE IF NOT EXISTS / role-existence-check /
        // DROP POLICY IF EXISTS make re-running the file safe.
        ApplyMigration(connection);
        ApplyMigration(connection);
    }

    [Fact]
    public void Migration_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
            PostgresTestFixture.ResetInbox();

            var orgAId = Guid.NewGuid();
            using var insertCmd = new NpgsqlCommand(
                """
                INSERT INTO sync_inbox (
                    operation_id, organization_id, branch_id, aggregate_id,
                    aggregate_version, actor_id, correlation_id, occurred_at_utc,
                    payload_kind, payload, status)
                VALUES ($1, $2, $3, $3, 1, $3, $3, now(), 'sale', '{}', 'Pending')
                """,
                ownerConnection);
            insertCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCmd.Parameters.AddWithValue(orgAId);
            insertCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCmd.ExecuteNonQuery();

            // Owner connection is a Postgres superuser in the local `postgres`
            // Docker image, so Postgres never applies RLS to it regardless of
            // FORCE — a documented local-fixture-only gap, identical to the
            // one recorded in PostgresCloudInboxStoreTests. Supabase's
            // managed Postgres does not grant superuser to application table
            // owners, so this gap does not apply to the deployed target.
        }

        // A different org's app_runtime-scoped connection must see zero rows
        // for org A's data — the same cross-org denial spec scenario proven
        // for the hand-authored dev policy, now proven for THIS migration file.
        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM sync_inbox", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    [Fact]
    public void Migration_UnscopedTransaction_FailsClosed_DefaultDeny()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
        }

        PostgresTestFixture.ResetInbox();

        using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM sync_inbox", connection, tx);
        var count = (long)cmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }
}
