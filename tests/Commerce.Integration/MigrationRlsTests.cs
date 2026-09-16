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

    private static string ResolveUsersMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0002_users.sql");
    }

    private static void ApplyUsersMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveUsersMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ResetUsers()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE user_directory, users", connection);
        cmd.ExecuteNonQuery();
    }

    private static string ResolveOrganizationsMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0003_organizations_branches.sql");
    }

    private static void ApplyOrganizationsMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveOrganizationsMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ResetOrganizations()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE branches, organizations", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-organization-persistence task 2.1: `0003` idempotency
    /// and cross-org isolation for `organizations` (policy compares `id`,
    /// not `organization_id`) and `branches` (symmetric policy on its own
    /// `organization_id`).
    /// </summary>
    [Fact]
    public void OrganizationsMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyMigration(connection);
        ApplyUsersMigration(connection);

        ApplyOrganizationsMigration(connection);
        ApplyOrganizationsMigration(connection);
    }

    [Fact]
    public void OrganizationsMigration_CrossOrganizationRead_ReturnsZeroRows_ForBothTables()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
            ApplyUsersMigration(ownerConnection);
            ApplyOrganizationsMigration(ownerConnection);
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", ownerConnection);
            insertBranchCmd.Parameters.AddWithValue(branchId);
            insertBranchCmd.Parameters.AddWithValue(orgAId);
            insertBranchCmd.ExecuteNonQuery();
            // Owner connection is a superuser in the local postgres image, so
            // RLS never applies to it regardless of FORCE — same documented
            // local-fixture-only gap as the 0001/0002 tests above.
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var orgCountCmd = new NpgsqlCommand("SELECT count(*) FROM organizations", scopedConnection, tx);
        var orgCount = (long)orgCountCmd.ExecuteScalar()!;

        using var branchCountCmd = new NpgsqlCommand("SELECT count(*) FROM branches", scopedConnection, tx);
        var branchCount = (long)branchCountCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, orgCount);
        Assert.Equal(0, branchCount);
    }

    [Fact]
    public void OrganizationsMigration_UnscopedTransaction_FailsClosed_DefaultDeny()
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
            ApplyUsersMigration(ownerConnection);
            ApplyOrganizationsMigration(ownerConnection);
        }

        ResetOrganizations();

        using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var orgCmd = new NpgsqlCommand("SELECT count(*) FROM organizations", connection, tx);
        var orgCount = (long)orgCmd.ExecuteScalar()!;
        using var branchCmd = new NpgsqlCommand("SELECT count(*) FROM branches", connection, tx);
        var branchCount = (long)branchCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, orgCount);
        Assert.Equal(0, branchCount);
    }

    [Fact]
    public void BranchesMigration_WithCheckViolatingInsert_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
            ApplyUsersMigration(ownerConnection);
            ApplyOrganizationsMigration(ownerConnection);
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();
        }

        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", writeConnection, tx))
        {
            // Scoped to a DIFFERENT org than orgAId, but the insert below
            // claims orgAId — WITH CHECK must reject this.
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var insertCmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Rogue Branch')",
            writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// Covers commerce-user-credentials task 1.1: `0002_users.sql` idempotency
    /// and cross-org isolation for `users` (symmetric policy) and
    /// `user_directory` (asymmetric: unscoped read allowed, scoped write
    /// enforced) — spec "User row is isolated by organization".
    /// </summary>
    [Fact]
    public void UsersMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyMigration(connection);

        ApplyUsersMigration(connection);
        ApplyUsersMigration(connection);
    }

    [Fact]
    public void UsersMigration_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
            ApplyUsersMigration(ownerConnection);
            ResetUsers();

            using var insertCmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles)
                VALUES ($1, $2, 'owner@example.com', 'hash', '{}', '[]')
                """, ownerConnection);
            insertCmd.Parameters.AddWithValue(userId);
            insertCmd.Parameters.AddWithValue(orgAId);
            insertCmd.ExecuteNonQuery();
            // Owner connection is a superuser in the local postgres image, so
            // RLS never applies to it regardless of FORCE — same documented
            // local-fixture-only gap as the 0001 tests above.
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM users", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    [Fact]
    public void UsersMigration_UnscopedTransaction_FailsClosed_DefaultDeny()
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
            ApplyUsersMigration(ownerConnection);
        }

        ResetUsers();

        using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM users", connection, tx);
        var count = (long)cmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    [Fact]
    public void UserDirectoryMigration_UnscopedRead_IsAllowed_ButWriteStaysOrgScoped()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyMigration(ownerConnection);
            ApplyUsersMigration(ownerConnection);
            ResetUsers();

            using var insertCmd = new NpgsqlCommand(
                "INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ('lookup@example.com', $1, $2)",
                ownerConnection);
            insertCmd.Parameters.AddWithValue(orgAId);
            insertCmd.Parameters.AddWithValue(userId);
            insertCmd.ExecuteNonQuery();
        }

        // Unscoped read (no set_config at all): user_directory's USING(true)
        // policy allows the sign-in email->org lookup before any tenant scope
        // exists.
        using (var readConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            readConnection.Open();
            using var tx = readConnection.BeginTransaction();
            using var readCmd = new NpgsqlCommand(
                "SELECT organization_id FROM user_directory WHERE email_normalized = 'lookup@example.com'",
                readConnection, tx);
            var result = readCmd.ExecuteScalar();
            tx.Commit();

            Assert.NotNull(result);
            Assert.Equal(orgAId, (Guid)result!);
        }

        // A write claiming a DIFFERENT org than the current scope is rejected
        // by WITH CHECK — writes stay org-scoped even though reads are global.
        using (var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            writeConnection.Open();
            using var tx = writeConnection.BeginTransaction();
            using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", writeConnection, tx))
            {
                scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
                scopeCmd.ExecuteNonQuery();
            }

            using var insertCmd = new NpgsqlCommand(
                "INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ('other@example.com', $1, $2)",
                writeConnection, tx);
            insertCmd.Parameters.AddWithValue(orgAId); // claims org A while scoped to a different org
            insertCmd.Parameters.AddWithValue(Guid.NewGuid());

            Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
            tx.Rollback();
        }
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
