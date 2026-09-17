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
        // CASCADE: device_credentials (0004) carries FKs to both organizations
        // and branches, so a plain TRUNCATE of these two tables fails once
        // any device_credentials row references them.
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE branches, organizations CASCADE", connection);
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

    private static string ResolveDeviceCredentialsMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0004_device_credentials.sql");
    }

    private static void ApplyDeviceCredentialsMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveDeviceCredentialsMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ResetDeviceCredentials()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE device_credentials", connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllPriorMigrations(NpgsqlConnection connection)
    {
        ApplyMigration(connection);
        ApplyUsersMigration(connection);
        ApplyOrganizationsMigration(connection);
        ApplyDeviceCredentialsMigration(connection);
    }

    /// <summary>
    /// Covers commerce-pos-installation-identity task 1.3: `0004` idempotency.
    /// </summary>
    [Fact]
    public void DeviceCredentialsMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllPriorMigrations(connection);

        ApplyDeviceCredentialsMigration(connection);
    }

    /// <summary>
    /// The asymmetry that makes verification possible: an UNSCOPED SELECT
    /// (no `set_config` at all) still returns the row.
    /// </summary>
    [Fact]
    public void DeviceCredentialsMigration_UnscopedSelect_ReturnsRow()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllPriorMigrations(ownerConnection);
            ResetDeviceCredentials();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", ownerConnection);
            insertBranchCmd.Parameters.AddWithValue(branchId);
            insertBranchCmd.Parameters.AddWithValue(orgId);
            insertBranchCmd.ExecuteNonQuery();

            using var insertCredentialCmd = new NpgsqlCommand(
                """
                INSERT INTO device_credentials
                    (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id)
                VALUES ('hash-unscoped-read', $1, $2, $3, $4, $5)
                """, ownerConnection);
            insertCredentialCmd.Parameters.AddWithValue(credentialId);
            insertCredentialCmd.Parameters.AddWithValue(orgId);
            insertCredentialCmd.Parameters.AddWithValue(branchId);
            insertCredentialCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCredentialCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCredentialCmd.ExecuteNonQuery();
        }

        using var readConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        readConnection.Open();
        using var tx = readConnection.BeginTransaction();
        using var readCmd = new NpgsqlCommand(
            "SELECT organization_id FROM device_credentials WHERE token_hash = 'hash-unscoped-read'",
            readConnection, tx);
        var result = readCmd.ExecuteScalar();
        tx.Commit();

        Assert.NotNull(result);
        Assert.Equal(orgId, (Guid)result!);
    }

    /// <summary>
    /// Cross-org INSERT must be rejected by `device_credentials_issue`'s
    /// WITH CHECK — a caller scoped to org B cannot insert a credential
    /// claiming org A.
    /// </summary>
    [Fact]
    public void DeviceCredentialsMigration_CrossOrgInsert_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllPriorMigrations(ownerConnection);
            ResetDeviceCredentials();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", ownerConnection);
            insertBranchCmd.Parameters.AddWithValue(branchAId);
            insertBranchCmd.Parameters.AddWithValue(orgAId);
            insertBranchCmd.ExecuteNonQuery();
        }

        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", writeConnection, tx))
        {
            // Scoped to a DIFFERENT org than orgAId, but the insert claims orgAId.
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO device_credentials
                (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id)
            VALUES ('hash-cross-org-insert', $1, $2, $3, $4, $5)
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);
        insertCmd.Parameters.AddWithValue(branchAId);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// The revoke policy's core trick: an UNSCOPED UPDATE (no `set_config` at
    /// all) that sets `is_revoked = true` MUST succeed — this is exactly what
    /// cross-org re-pairing needs (revoke the org-A row while scoped to org B,
    /// i.e. before any org-A scope is representable).
    /// </summary>
    [Fact]
    public void DeviceCredentialsMigration_UnscopedUpdateToRevoked_Succeeds()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllPriorMigrations(ownerConnection);
            ResetDeviceCredentials();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", ownerConnection);
            insertBranchCmd.Parameters.AddWithValue(branchId);
            insertBranchCmd.Parameters.AddWithValue(orgId);
            insertBranchCmd.ExecuteNonQuery();

            using var insertCredentialCmd = new NpgsqlCommand(
                """
                INSERT INTO device_credentials
                    (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id)
                VALUES ('hash-unscoped-revoke', $1, $2, $3, $4, $5)
                """, ownerConnection);
            insertCredentialCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCredentialCmd.Parameters.AddWithValue(orgId);
            insertCredentialCmd.Parameters.AddWithValue(branchId);
            insertCredentialCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCredentialCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCredentialCmd.ExecuteNonQuery();
        }

        // No set_config at all: fully unscoped session/transaction.
        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using var updateCmd = new NpgsqlCommand(
            "UPDATE device_credentials SET is_revoked = true, revoked_at = now() WHERE token_hash = 'hash-unscoped-revoke'",
            writeConnection, tx);
        var rows = updateCmd.ExecuteNonQuery();
        tx.Commit();

        Assert.Equal(1, rows);
    }

    /// <summary>
    /// The structural half of the trick: an UNSCOPED UPDATE that would leave
    /// the row NOT revoked (an un-revoke, or any field rewrite that doesn't
    /// also revoke) MUST be rejected by `WITH CHECK (is_revoked)` — this is
    /// what makes an unscoped un-revoke/rewrite unrepresentable, not merely
    /// untested.
    /// </summary>
    [Fact]
    public void DeviceCredentialsMigration_UnscopedUpdateToUnrevokeOrRewrite_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var otherBranchId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllPriorMigrations(ownerConnection);
            ResetDeviceCredentials();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranch1Cmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", ownerConnection);
            insertBranch1Cmd.Parameters.AddWithValue(branchId);
            insertBranch1Cmd.Parameters.AddWithValue(orgId);
            insertBranch1Cmd.ExecuteNonQuery();

            using var insertBranch2Cmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Second')", ownerConnection);
            insertBranch2Cmd.Parameters.AddWithValue(otherBranchId);
            insertBranch2Cmd.Parameters.AddWithValue(orgId);
            insertBranch2Cmd.ExecuteNonQuery();

            using var insertRevokedCmd = new NpgsqlCommand(
                """
                INSERT INTO device_credentials
                    (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id, is_revoked, revoked_at)
                VALUES ('hash-already-revoked', $1, $2, $3, $4, $5, true, now())
                """, ownerConnection);
            insertRevokedCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertRevokedCmd.Parameters.AddWithValue(orgId);
            insertRevokedCmd.Parameters.AddWithValue(branchId);
            insertRevokedCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertRevokedCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertRevokedCmd.ExecuteNonQuery();

            using var insertLiveCmd = new NpgsqlCommand(
                """
                INSERT INTO device_credentials
                    (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id)
                VALUES ('hash-live-rewrite-target', $1, $2, $3, $4, $5)
                """, ownerConnection);
            insertLiveCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertLiveCmd.Parameters.AddWithValue(orgId);
            insertLiveCmd.Parameters.AddWithValue(branchId);
            insertLiveCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertLiveCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertLiveCmd.ExecuteNonQuery();
        }

        // Attempt 1: unscoped un-revoke — the resulting row is NOT revoked.
        using (var unrevokeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            unrevokeConnection.Open();
            using var tx = unrevokeConnection.BeginTransaction();
            using var updateCmd = new NpgsqlCommand(
                "UPDATE device_credentials SET is_revoked = false WHERE token_hash = 'hash-already-revoked'",
                unrevokeConnection, tx);

            Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            tx.Rollback();
        }

        // Attempt 2: unscoped field rewrite (branch_id) that does NOT also
        // revoke the row — must be rejected too, not just an un-revoke.
        using (var rewriteConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            rewriteConnection.Open();
            using var tx = rewriteConnection.BeginTransaction();
            using var updateCmd = new NpgsqlCommand(
                "UPDATE device_credentials SET branch_id = $1 WHERE token_hash = 'hash-live-rewrite-target'",
                rewriteConnection, tx);
            updateCmd.Parameters.AddWithValue(otherBranchId);

            Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            tx.Rollback();
        }
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

    // --- commerce-password-recovery: 0005_password_recovery.sql -----------

    private static string ResolvePasswordRecoveryMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0005_password_recovery.sql");
    }

    private static void ApplyPasswordRecoveryMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolvePasswordRecoveryMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ResetPasswordRecovery()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE password_reset_tokens", connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0005(NpgsqlConnection connection)
    {
        ApplyAllPriorMigrations(connection);
        ApplyPasswordRecoveryMigration(connection);
    }

    /// <summary>
    /// Covers commerce-password-recovery task 1.1: `0005` idempotency.
    /// </summary>
    [Fact]
    public void PasswordRecoveryMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllPriorMigrations(connection);

        ApplyPasswordRecoveryMigration(connection);
        ApplyPasswordRecoveryMigration(connection);
    }

    /// <summary>
    /// Cross-org INSERT must be rejected by `password_reset_tokens_issue`'s
    /// WITH CHECK — a caller scoped to org B cannot insert a token claiming
    /// org A.
    /// </summary>
    [Fact]
    public void PasswordRecoveryMigration_CrossOrgInsert_Throws()
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
            ApplyAllMigrationsThrough0005(ownerConnection);
            ResetPasswordRecovery();
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
            // Scoped to a DIFFERENT org than orgAId, but the insert claims orgAId.
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var insertCmd = new NpgsqlCommand(
            """
            INSERT INTO password_reset_tokens (token_hash, user_id, organization_id, expires_at)
            VALUES ('hash-cross-org-insert', $1, $2, now() + interval '1 hour')
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// The lookup asymmetry: an UNSCOPED SELECT (no `set_config` at all)
    /// still resolves the row — confirm has no org until it reads the token.
    /// </summary>
    [Fact]
    public void PasswordRecoveryMigration_UnscopedSelect_ReturnsRow()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0005(ownerConnection);
            ResetPasswordRecovery();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertTokenCmd = new NpgsqlCommand(
                """
                INSERT INTO password_reset_tokens (token_hash, user_id, organization_id, expires_at)
                VALUES ('hash-unscoped-read', $1, $2, now() + interval '1 hour')
                """, ownerConnection);
            insertTokenCmd.Parameters.AddWithValue(userId);
            insertTokenCmd.Parameters.AddWithValue(orgId);
            insertTokenCmd.ExecuteNonQuery();
        }

        using var readConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        readConnection.Open();
        using var tx = readConnection.BeginTransaction();
        using var readCmd = new NpgsqlCommand(
            "SELECT organization_id FROM password_reset_tokens WHERE token_hash = 'hash-unscoped-read'",
            readConnection, tx);
        var result = readCmd.ExecuteScalar();
        tx.Commit();

        Assert.NotNull(result);
        Assert.Equal(orgId, (Guid)result!);
    }

    /// <summary>
    /// Unscoped UPDATE that consumes the token (sets `consumed_at`) MUST
    /// succeed — confirm has no org scope until the token row is read.
    /// </summary>
    [Fact]
    public void PasswordRecoveryMigration_UnscopedUpdateToConsumed_Succeeds()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0005(ownerConnection);
            ResetPasswordRecovery();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertTokenCmd = new NpgsqlCommand(
                """
                INSERT INTO password_reset_tokens (token_hash, user_id, organization_id, expires_at)
                VALUES ('hash-unscoped-consume', $1, $2, now() + interval '1 hour')
                """, ownerConnection);
            insertTokenCmd.Parameters.AddWithValue(userId);
            insertTokenCmd.Parameters.AddWithValue(orgId);
            insertTokenCmd.ExecuteNonQuery();
        }

        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using var updateCmd = new NpgsqlCommand(
            "UPDATE password_reset_tokens SET consumed_at = now() WHERE token_hash = 'hash-unscoped-consume'",
            writeConnection, tx);
        var rows = updateCmd.ExecuteNonQuery();
        tx.Commit();

        Assert.Equal(1, rows);
    }

    /// <summary>
    /// An unscoped UPDATE that does NOT set `consumed_at` (an un-consume, or
    /// any other field rewrite) MUST be rejected — structurally
    /// unrepresentable, not merely untested.
    /// </summary>
    [Fact]
    public void PasswordRecoveryMigration_UnscopedUpdateNotConsuming_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0005(ownerConnection);
            ResetPasswordRecovery();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertTokenCmd = new NpgsqlCommand(
                """
                INSERT INTO password_reset_tokens (token_hash, user_id, organization_id, expires_at, consumed_at)
                VALUES ('hash-already-consumed', $1, $2, now() + interval '1 hour', now())
                """, ownerConnection);
            insertTokenCmd.Parameters.AddWithValue(userId);
            insertTokenCmd.Parameters.AddWithValue(orgId);
            insertTokenCmd.ExecuteNonQuery();
        }

        // Unscoped un-consume: the resulting row would NOT be consumed.
        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using var updateCmd = new NpgsqlCommand(
            "UPDATE password_reset_tokens SET consumed_at = NULL WHERE token_hash = 'hash-already-consumed'",
            writeConnection, tx);

        Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
        tx.Rollback();
    }
}
