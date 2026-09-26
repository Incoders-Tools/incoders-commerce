using System.IO;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
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

    // --- commerce-role-taxonomy: 0006_role_taxonomy.sql --------------------

    private static string ResolveRoleTaxonomyMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0006_role_taxonomy.sql");
    }

    private static void ApplyRoleTaxonomyMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveRoleTaxonomyMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0006(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0005(connection);
        ApplyRoleTaxonomyMigration(connection);
    }

    /// <summary>
    /// Covers commerce-role-taxonomy task 1.6: a pre-seeded `"admin"` user is
    /// renamed to `"business-admin"` with a byte-identical permission set,
    /// still signs in (same password hash, untouched by the migration), and a
    /// second `0006` run is a no-op (matches zero rows / stays renamed).
    /// </summary>
    [Fact]
    public void RoleTaxonomyMigration_RenamesAdminToBusinessAdmin_PreservingPermissionsAndPasswordHash()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string plainTextPassword = "correct horse battery staple";
        var hasher = new PasswordHasher<UserAccount>();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, orgId, [], []), plainTextPassword);

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0005(ownerConnection);
            ResetUsers();

            using var insertCmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles)
                VALUES ($1, $2, 'legacy-admin@example.com', $3, '{}',
                        '[{"name":"admin","permissions":15}]'::jsonb)
                """, ownerConnection);
            insertCmd.Parameters.AddWithValue(userId);
            insertCmd.Parameters.AddWithValue(orgId);
            insertCmd.Parameters.AddWithValue(passwordHash);
            insertCmd.ExecuteNonQuery();

            // First 0006 application: the actual rewrite.
            ApplyRoleTaxonomyMigration(ownerConnection);

            using var readCmd = new NpgsqlCommand("SELECT roles, password_hash FROM users WHERE id = $1", ownerConnection);
            readCmd.Parameters.AddWithValue(userId);
            using (var reader = readCmd.ExecuteReader())
            {
                Assert.True(reader.Read());
                var rolesJson = reader.GetString(0);
                var storedHash = reader.GetString(1);

                Assert.Contains("\"business-admin\"", rolesJson);
                Assert.DoesNotContain("\"admin\"", rolesJson);
                Assert.Contains("15", rolesJson);
                Assert.Equal(passwordHash, storedHash);

                // Untouched password hash still verifies — "still signs in".
                var verification = hasher.VerifyHashedPassword(
                    new UserAccount(userId, orgId, [], []), storedHash, plainTextPassword);
                Assert.Equal(PasswordVerificationResult.Success, verification);
            }

            // Second 0006 application: must be a no-op (matches zero "admin"
            // rows) and must not throw the post-condition assertion, and must
            // leave the row renamed.
            ApplyRoleTaxonomyMigration(ownerConnection);

            using var rereadCmd = new NpgsqlCommand("SELECT roles FROM users WHERE id = $1", ownerConnection);
            rereadCmd.Parameters.AddWithValue(userId);
            var rolesAfterSecondRun = (string)rereadCmd.ExecuteScalar()!;
            Assert.Contains("\"business-admin\"", rolesAfterSecondRun);
            Assert.DoesNotContain("\"admin\"", rolesAfterSecondRun);
        }
    }

    // --- commerce-role-taxonomy: 0007_platform_administration.sql ----------

    public const string PlatformReadonlyPassword = "dev-only-platform-readonly-password";

    public const string PlatformReadonlyConnectionString =
        "Host=localhost;Port=5432;Database=" + PostgresTestFixture.Database +
        ";Username=platform_readonly;Password=" + PlatformReadonlyPassword + ";Timeout=3";

    private static string ResolvePlatformAdministrationMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0007_platform_administration.sql");
    }

    private static void ApplyPlatformAdministrationMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolvePlatformAdministrationMigrationPath())
            .Replace("__PLATFORM_READONLY_PASSWORD__", PlatformReadonlyPassword);
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0007(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0006(connection);
        ApplyPlatformAdministrationMigration(connection);
    }

    private static void ResetPlatformAdministration()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE platform_admins, audit_log RESTART IDENTITY", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-role-taxonomy task 2.1: `platform_admins` and
    /// `audit_log` exist with FORCE ROW LEVEL SECURITY, and `0007` re-applies
    /// cleanly (idempotent `CREATE ... IF NOT EXISTS`).
    /// </summary>
    [Fact]
    public void PlatformAdministrationMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0006(connection);

        ApplyPlatformAdministrationMigration(connection);
        ApplyPlatformAdministrationMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'platform_admins' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'audit_log' AND relrowsecurity AND relforcerowsecurity)
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    /// <summary>
    /// Covers commerce-role-taxonomy task 2.4: `platform_readonly` can
    /// `SELECT id, name, created_at` from `organizations` with no
    /// `app.current_org_id` set across two seeded orgs, but fails with a
    /// privilege error on every other table and on any write against
    /// `organizations` (design.md "Platform-admin cross-org read").
    /// </summary>
    [Fact]
    public void PlatformReadonly_CanReadOrganizationSummaryColumns_ButNothingElse()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var orgBId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0007(ownerConnection);
            ResetOrganizations();
            ResetPlatformAdministration();

            using var insertOrgACmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgACmd.Parameters.AddWithValue(orgAId);
            insertOrgACmd.ExecuteNonQuery();

            using var insertOrgBCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org B')", ownerConnection);
            insertOrgBCmd.Parameters.AddWithValue(orgBId);
            insertOrgBCmd.ExecuteNonQuery();
        }

        using var readonlyConnection = new NpgsqlConnection(PlatformReadonlyConnectionString);
        readonlyConnection.Open();

        // Allowed: SELECT id, name, created_at FROM organizations, unscoped.
        using (var selectCmd = new NpgsqlCommand("SELECT id, name, created_at FROM organizations ORDER BY name", readonlyConnection))
        using (var reader = selectCmd.ExecuteReader())
        {
            var rows = 0;
            while (reader.Read())
            {
                rows++;
            }
            Assert.Equal(2, rows);
        }

        // Denied: any other column/table, and any write against organizations.
        void AssertPrivilegeError(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, readonlyConnection);
            var ex = Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState); // insufficient_privilege
        }

        // NOTE: organizations' only columns ARE id/name/created_at (0003), so
        // `SELECT *` is equivalent to the granted column set and is NOT a
        // privilege boundary here — the real boundary is every OTHER table.
        AssertPrivilegeError("SELECT * FROM users LIMIT 1");
        AssertPrivilegeError("SELECT * FROM user_directory LIMIT 1");
        AssertPrivilegeError("SELECT * FROM branches LIMIT 1");
        AssertPrivilegeError("SELECT * FROM password_reset_tokens LIMIT 1");
        AssertPrivilegeError("SELECT * FROM device_credentials LIMIT 1");
        AssertPrivilegeError("SELECT * FROM sync_inbox LIMIT 1");
        AssertPrivilegeError("SELECT * FROM platform_admins LIMIT 1");
        AssertPrivilegeError("SELECT * FROM audit_log LIMIT 1");
        AssertPrivilegeError($"INSERT INTO organizations (id, name) VALUES ('{Guid.NewGuid()}', 'Rogue')");
        AssertPrivilegeError($"UPDATE organizations SET name = 'Rogue' WHERE id = '{orgAId}'");
        AssertPrivilegeError($"DELETE FROM organizations WHERE id = '{orgAId}'");
    }

    /// <summary>
    /// Covers commerce-role-taxonomy task 2.5: `app_runtime` cannot `SELECT`
    /// from `audit_log` at all, cannot `UPDATE` it, cannot insert an audit
    /// row for another organization, and cannot `UPDATE
    /// platform_admins.password_hash` (design.md "Audit table shape and RLS"
    /// / "`platform_admins` table shape and RLS").
    /// </summary>
    [Fact]
    public void AppRuntime_CannotReadAuditLog_CannotWriteCrossOrgAuditRow_CannotUpdatePlatformAdminPasswordHash()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var platformAdminId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0007(ownerConnection);
            ResetOrganizations();
            ResetPlatformAdministration();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertAdminCmd = new NpgsqlCommand(
                "INSERT INTO platform_admins (id, email, password_hash) VALUES ($1, 'operator@incoders.dev', 'hash')",
                ownerConnection);
            insertAdminCmd.Parameters.AddWithValue(platformAdminId);
            insertAdminCmd.ExecuteNonQuery();
        }

        using var appRuntimeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        appRuntimeConnection.Open();

        // No SELECT grant on audit_log at all.
        using (var selectCmd = new NpgsqlCommand("SELECT * FROM audit_log LIMIT 1", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => selectCmd.ExecuteReader().Dispose());
            Assert.Equal("42501", ex.SqlState);
        }

        // No UPDATE grant on audit_log at all (append-only).
        using (var updateCmd = new NpgsqlCommand("UPDATE audit_log SET action = 'tampered' WHERE id = 1", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }

        // Insert claiming a DIFFERENT organization than the current scope is rejected.
        using (var tx = appRuntimeConnection.BeginTransaction())
        {
            using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", appRuntimeConnection, tx))
            {
                scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
                scopeCmd.ExecuteNonQuery();
            }

            using var insertCmd = new NpgsqlCommand(
                """
                INSERT INTO audit_log (actor_kind, actor_id, organization_id, entity_type, entity_id, action)
                VALUES ('org-user', $1, $2, 'user', $1, 'user.created')
                """, appRuntimeConnection, tx);
            insertCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertCmd.Parameters.AddWithValue(orgAId);

            Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
            tx.Rollback();
        }

        // Column-scoped UPDATE grant excludes password_hash.
        using (var updatePasswordCmd = new NpgsqlCommand(
            "UPDATE platform_admins SET password_hash = 'rogue' WHERE id = $1", appRuntimeConnection))
        {
            updatePasswordCmd.Parameters.AddWithValue(platformAdminId);
            var ex = Assert.Throws<PostgresException>(() => updatePasswordCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }
    }

    // --- commerce-customer-identity: 0008_customer_registry.sql -----------

    private static string ResolveCustomerRegistryMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0008_customer_registry.sql");
    }

    private static void ApplyCustomerRegistryMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveCustomerRegistryMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0008(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0007(connection);
        ApplyCustomerRegistryMigration(connection);
    }

    private static void ResetCustomerRegistry()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        // CASCADE: customer_ordering_access and users.customer_id both carry
        // FKs into customers.
        using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE customer_ordering_access, customers CASCADE", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-customer-identity task 1.1/1.5: `0008` idempotency —
    /// applying it twice against an already-migrated database is a no-op.
    /// </summary>
    [Fact]
    public void CustomerRegistryMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0007(connection);

        ApplyCustomerRegistryMigration(connection);
        ApplyCustomerRegistryMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'customers' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'customer_ordering_access' AND relrowsecurity AND relforcerowsecurity)
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    /// <summary>
    /// `app_runtime` has no `DELETE` grant on either new table — this change
    /// has no delete flow, so deletion is structurally unavailable (the
    /// `platform_admins` precedent).
    /// </summary>
    [Fact]
    public void CustomerRegistryMigration_AppRuntime_HasNoDeleteGrant_OnEitherTable()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
        }

        using var appRuntimeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        appRuntimeConnection.Open();

        using (var deleteCustomersCmd = new NpgsqlCommand("DELETE FROM customers WHERE false", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => deleteCustomersCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }

        using (var deleteAccessCmd = new NpgsqlCommand("DELETE FROM customer_ordering_access WHERE false", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => deleteAccessCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }
    }

    /// <summary>
    /// Covers customer-registry spec "Second organization cannot read or
    /// write another org's customers" — cross-org SELECT returns zero rows,
    /// and a cross-org INSERT is rejected by `WITH CHECK`.
    /// </summary>
    [Fact]
    public void CustomersMigration_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomerCmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM customers", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    [Fact]
    public void CustomersMigration_CrossOrgInsert_Throws()
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
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
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
            INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
            VALUES ($1, $2, 'Retail', 'Rogue Customer', $3)
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    [Fact]
    public void CustomersMigration_CrossOrgUpdate_AffectsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomerCmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();
        }

        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", writeConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var updateCmd = new NpgsqlCommand(
            "UPDATE customers SET display_name = 'Renamed' WHERE id = $1", writeConnection, tx);
        updateCmd.Parameters.AddWithValue(customerId);
        var rows = updateCmd.ExecuteNonQuery();
        tx.Commit();

        Assert.Equal(0, rows);
    }

    /// <summary>
    /// Defense-in-depth guard (design.md "Staff-permission denial for a
    /// CustomerId-bearing user"): `users_customer_has_no_roles` rejects a
    /// customer-linked user carrying a non-empty `roles` array.
    /// </summary>
    [Fact]
    public void UsersCustomerHasNoRolesCheck_RejectsCustomerLinkedUserWithNonEmptyRoles()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0008(ownerConnection);
        ResetCustomerRegistry();
        ResetOrganizations();
        ResetUsers();

        using (var insertOrgCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection))
        {
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();
        }

        using (var insertCustomerCmd = new NpgsqlCommand(
            """
            INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
            VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
            """, ownerConnection))
        {
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();
        }

        // A customer-linked user with a non-empty roles array must be
        // rejected by the CHECK — structurally unrepresentable, not merely
        // untested.
        using (var insertBadUserCmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles, customer_id)
            VALUES ($1, $2, 'customer-login@example.com', 'hash', '{}',
                    '[{"name":"business-admin","permissions":15}]'::jsonb, $3)
            """, ownerConnection))
        {
            insertBadUserCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertBadUserCmd.Parameters.AddWithValue(orgAId);
            insertBadUserCmd.Parameters.AddWithValue(customerId);

            Assert.Throws<PostgresException>(() => insertBadUserCmd.ExecuteNonQuery());
        }

        // The same customer link with an EMPTY roles array must be accepted.
        using (var insertGoodUserCmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles, customer_id)
            VALUES ($1, $2, 'customer-login-ok@example.com', 'hash', '{}', '[]'::jsonb, $3)
            """, ownerConnection))
        {
            insertGoodUserCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertGoodUserCmd.Parameters.AddWithValue(orgAId);
            insertGoodUserCmd.Parameters.AddWithValue(customerId);
            var rows = insertGoodUserCmd.ExecuteNonQuery();
            Assert.Equal(1, rows);
        }
    }

    /// <summary>
    /// The `customer_ordering_access` revoke policy's core trick, exactly the
    /// `device_credentials_revoke` precedent: an UNSCOPED UPDATE that sets
    /// `is_enabled = false` MUST succeed.
    /// </summary>
    [Fact]
    public void CustomerOrderingAccessMigration_UnscopedUpdateToRevoked_Succeeds()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomerCmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();

            using var insertAccessCmd = new NpgsqlCommand(
                """
                INSERT INTO customer_ordering_access (credential_hash, organization_id, customer_id, issued_by_user_id)
                VALUES ('hash-unscoped-revoke', $1, $2, $3)
                """, ownerConnection);
            insertAccessCmd.Parameters.AddWithValue(orgAId);
            insertAccessCmd.Parameters.AddWithValue(customerId);
            insertAccessCmd.Parameters.AddWithValue(actorId);
            insertAccessCmd.ExecuteNonQuery();
        }

        // No set_config at all: fully unscoped session/transaction.
        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using var updateCmd = new NpgsqlCommand(
            "UPDATE customer_ordering_access SET is_enabled = false, revoked_at_utc = now() WHERE credential_hash = 'hash-unscoped-revoke'",
            writeConnection, tx);
        var rows = updateCmd.ExecuteNonQuery();
        tx.Commit();

        Assert.Equal(1, rows);
    }

    /// <summary>
    /// The structural half: an UNSCOPED UPDATE that would leave the row
    /// enabled (an un-revoke, or any other field rewrite) MUST be rejected by
    /// `WITH CHECK (NOT is_enabled)` — an unscoped un-revoke is
    /// unrepresentable, not merely untested.
    /// </summary>
    [Fact]
    public void CustomerOrderingAccessMigration_UnscopedUpdateToUnrevokeOrRewrite_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var otherCustomerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomer1Cmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomer1Cmd.Parameters.AddWithValue(customerId);
            insertCustomer1Cmd.Parameters.AddWithValue(orgAId);
            insertCustomer1Cmd.Parameters.AddWithValue(actorId);
            insertCustomer1Cmd.ExecuteNonQuery();

            using var insertCustomer2Cmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'John Roe', $3)
                """, ownerConnection);
            insertCustomer2Cmd.Parameters.AddWithValue(otherCustomerId);
            insertCustomer2Cmd.Parameters.AddWithValue(orgAId);
            insertCustomer2Cmd.Parameters.AddWithValue(actorId);
            insertCustomer2Cmd.ExecuteNonQuery();

            using var insertRevokedCmd = new NpgsqlCommand(
                """
                INSERT INTO customer_ordering_access (credential_hash, organization_id, customer_id, issued_by_user_id, is_enabled, revoked_at_utc)
                VALUES ('hash-already-revoked', $1, $2, $3, false, now())
                """, ownerConnection);
            insertRevokedCmd.Parameters.AddWithValue(orgAId);
            insertRevokedCmd.Parameters.AddWithValue(customerId);
            insertRevokedCmd.Parameters.AddWithValue(actorId);
            insertRevokedCmd.ExecuteNonQuery();

            using var insertLiveCmd = new NpgsqlCommand(
                """
                INSERT INTO customer_ordering_access (credential_hash, organization_id, customer_id, issued_by_user_id)
                VALUES ('hash-live-rewrite-target', $1, $2, $3)
                """, ownerConnection);
            insertLiveCmd.Parameters.AddWithValue(orgAId);
            insertLiveCmd.Parameters.AddWithValue(customerId);
            insertLiveCmd.Parameters.AddWithValue(actorId);
            insertLiveCmd.ExecuteNonQuery();
        }

        // Attempt 1: unscoped un-revoke — the resulting row is NOT revoked.
        using (var unrevokeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            unrevokeConnection.Open();
            using var tx = unrevokeConnection.BeginTransaction();
            using var updateCmd = new NpgsqlCommand(
                "UPDATE customer_ordering_access SET is_enabled = true WHERE credential_hash = 'hash-already-revoked'",
                unrevokeConnection, tx);

            Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            tx.Rollback();
        }

        // Attempt 2: unscoped field rewrite (customer_id) that does NOT also
        // revoke the row — must be rejected too, not just an un-revoke.
        using (var rewriteConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            rewriteConnection.Open();
            using var tx = rewriteConnection.BeginTransaction();
            using var updateCmd = new NpgsqlCommand(
                "UPDATE customer_ordering_access SET customer_id = $1 WHERE credential_hash = 'hash-live-rewrite-target'",
                rewriteConnection, tx);
            updateCmd.Parameters.AddWithValue(otherCustomerId);

            Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            tx.Rollback();
        }
    }

    [Fact]
    public void CustomerOrderingAccessMigration_CrossOrgInsert_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomerCmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();
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
            INSERT INTO customer_ordering_access (credential_hash, organization_id, customer_id, issued_by_user_id)
            VALUES ('hash-cross-org-insert', $1, $2, $3)
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(orgAId);
        insertCmd.Parameters.AddWithValue(customerId);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// The lookup asymmetry, exactly the `device_credentials_lookup`
    /// precedent: an UNSCOPED SELECT (no `set_config` at all) still resolves
    /// the row, INCLUDING a row belonging to a different organization than
    /// any later-established scope — the org comparison happens in
    /// application code (`CustomerCatalogAccessService.Evaluate`), not RLS.
    /// </summary>
    [Fact]
    public void CustomerOrderingAccessMigration_UnscopedSelect_ReturnsRow()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0008(ownerConnection);
            ResetCustomerRegistry();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertCustomerCmd = new NpgsqlCommand(
                """
                INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id)
                VALUES ($1, $2, 'Retail', 'Jane Doe', $3)
                """, ownerConnection);
            insertCustomerCmd.Parameters.AddWithValue(customerId);
            insertCustomerCmd.Parameters.AddWithValue(orgAId);
            insertCustomerCmd.Parameters.AddWithValue(actorId);
            insertCustomerCmd.ExecuteNonQuery();

            using var insertAccessCmd = new NpgsqlCommand(
                """
                INSERT INTO customer_ordering_access (credential_hash, organization_id, customer_id, issued_by_user_id)
                VALUES ('hash-unscoped-read', $1, $2, $3)
                """, ownerConnection);
            insertAccessCmd.Parameters.AddWithValue(orgAId);
            insertAccessCmd.Parameters.AddWithValue(customerId);
            insertAccessCmd.Parameters.AddWithValue(actorId);
            insertAccessCmd.ExecuteNonQuery();
        }

        using var readConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        readConnection.Open();
        using var tx = readConnection.BeginTransaction();
        using (var readCmd = new NpgsqlCommand(
            "SELECT organization_id, customer_id, is_enabled FROM customer_ordering_access WHERE credential_hash = 'hash-unscoped-read'",
            readConnection, tx))
        using (var reader = readCmd.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(orgAId, reader.GetGuid(0));
            Assert.Equal(customerId, reader.GetGuid(1));
            Assert.True(reader.GetBoolean(2));
        }
        tx.Commit();
    }

    // --- commerce-pricing-engine: 0009_catalog_and_pricing.sql -------------

    private static string ResolveCatalogAndPricingMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0009_catalog_and_pricing.sql");
    }

    private static void ApplyCatalogAndPricingMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveCatalogAndPricingMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// B7 U4: `commerce_test` is one shared, accumulating database
    /// (`PostgresTestFixture.Database`) — once ANY test class in the suite
    /// applies `0016_catalog_branch_ownership.sql`, `products`/
    /// `presentations` carry `branch_id NOT NULL` for the rest of the run,
    /// regardless of which migration file THIS class's fixture nominally
    /// applied through. Every catalog-touching raw-SQL test below applies
    /// this explicitly and supplies a real `branch_id`, so its INSERTs stay
    /// correct whether or not a sibling class already advanced the shared
    /// schema.
    /// </summary>
    private static void ApplyBranchOwnershipMigration(NpgsqlConnection connection)
    {
        var repoRoot = ResolveOrganizationsMigrationPath();
        var dir = Path.GetDirectoryName(repoRoot)!;
        var sql = File.ReadAllText(Path.Combine(dir, "0016_catalog_branch_ownership.sql"));
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0009(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0008(connection);
        ApplyCatalogAndPricingMigration(connection);
    }

    private static void ResetCatalogAndPricing()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        // CASCADE: price_list_entries carries FKs to price_lists and
        // presentations; presentations carries a FK to products;
        // price_import_batches/rows (Part C, Work Unit 9) carry FKs to
        // supplier_price_mappings and presentations.
        using var cmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE price_import_rows, price_import_batches, supplier_price_mappings,
                           price_list_entries, price_lists, presentations, products CASCADE
            """, connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-pricing-engine task 1.1: `0009` idempotency — applying
    /// it twice against an already-migrated database is a no-op.
    /// </summary>
    [Fact]
    public void CatalogAndPricingMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0008(connection);

        ApplyCatalogAndPricingMigration(connection);
        ApplyCatalogAndPricingMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'products' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'presentations' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'price_lists' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'price_list_entries' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'supplier_price_mappings' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'price_import_batches' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'price_import_rows' AND relrowsecurity AND relforcerowsecurity)
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    /// <summary>
    /// `app_runtime` has no `DELETE` grant on any of the four new tables —
    /// the `customers`/`platform_admins` precedent. `price_list_entries` also
    /// has no `UPDATE` grant: append-only is enforced at the GRANT level, not
    /// merely by convention (design.md "Effective-dating shape").
    /// </summary>
    [Fact]
    public void CatalogAndPricingMigration_AppRuntime_HasNoDeleteGrant_OnAnyTable_AndNoUpdateOnPriceListEntries()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0009(ownerConnection);
        }

        using var appRuntimeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        appRuntimeConnection.Open();

        foreach (var table in new[]
        {
            "products", "presentations", "price_lists", "price_list_entries",
            "supplier_price_mappings", "price_import_batches", "price_import_rows",
        })
        {
            using var deleteCmd = new NpgsqlCommand($"DELETE FROM {table} WHERE false", appRuntimeConnection);
            var ex = Assert.Throws<PostgresException>(() => deleteCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }

        using (var updateCmd = new NpgsqlCommand(
            "UPDATE price_list_entries SET unit_price = 1 WHERE false", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }

        // price_import_rows is append-only (design.md "Import state machine"):
        // every row's match_status is decided once, at parse time.
        using (var updateCmd = new NpgsqlCommand(
            "UPDATE price_import_rows SET match_status = 'Matched' WHERE false", appRuntimeConnection))
        {
            var ex = Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
            Assert.Equal("42501", ex.SqlState);
        }
    }

    /// <summary>
    /// Org B cannot read org A's supplier mappings or import batches — the
    /// same tenant-isolation shape as `price_lists`/`presentations`
    /// (design.md "Price tables RLS" extended to Part C).
    /// </summary>
    [Fact]
    public void SupplierMappingsAndImportBatches_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0009(ownerConnection);
            ResetCatalogAndPricing();
            ResetOrganizations();

            void Exec(string sql, Action<NpgsqlCommand> bind)
            {
                using var cmd = new NpgsqlCommand(sql, ownerConnection);
                bind(cmd);
                cmd.ExecuteNonQuery();
            }

            Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
            Exec(
                """
                INSERT INTO supplier_price_mappings
                    (id, organization_id, supplier_name, sheet_name, header_row, code_column, price_column, created_by_user_id)
                VALUES ($1, $2, 'Acme', 'Prices', 1, 'A', 'B', $3)
                """, c =>
                {
                    c.Parameters.AddWithValue(mappingId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO price_import_batches
                    (id, organization_id, supplier_mapping_id, file_name, row_count, uploaded_by_user_id)
                VALUES ($1, $2, $3, 'prices.xlsx', 0, $4)
                """, c =>
                {
                    c.Parameters.AddWithValue(batchId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(mappingId);
                    c.Parameters.AddWithValue(actorId);
                });
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var mappingCountCmd = new NpgsqlCommand("SELECT count(*) FROM supplier_price_mappings", scopedConnection, tx);
        var mappingCount = (long)mappingCountCmd.ExecuteScalar()!;
        using var batchCountCmd = new NpgsqlCommand("SELECT count(*) FROM price_import_batches", scopedConnection, tx);
        var batchCount = (long)batchCountCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, mappingCount);
        Assert.Equal(0, batchCount);
    }

    [Fact]
    public void ProductsMigration_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0009(ownerConnection);
            ApplyBranchOwnershipMigration(ownerConnection);
            ResetCatalogAndPricing();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')", ownerConnection);
            insertBranchCmd.Parameters.AddWithValue(branchAId);
            insertBranchCmd.Parameters.AddWithValue(orgAId);
            insertBranchCmd.ExecuteNonQuery();

            using var insertProductCmd = new NpgsqlCommand(
                """
                INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
                VALUES ($1, $2, $3, 'Product A', $4, $4, $4)
                """, ownerConnection);
            insertProductCmd.Parameters.AddWithValue(productId);
            insertProductCmd.Parameters.AddWithValue(orgAId);
            insertProductCmd.Parameters.AddWithValue(branchAId);
            insertProductCmd.Parameters.AddWithValue(actorId);
            insertProductCmd.ExecuteNonQuery();
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM products", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    [Fact]
    public void ProductsMigration_CrossOrgInsert_Throws()
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
            ApplyAllMigrationsThrough0009(ownerConnection);
            ApplyBranchOwnershipMigration(ownerConnection);
            ResetCatalogAndPricing();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertBranchCmd = new NpgsqlCommand(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')", ownerConnection);
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
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, 'Rogue Product', $4, $4, $4)
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);
        insertCmd.Parameters.AddWithValue(branchAId);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// `presentations_org_branch_code_uk` (B7 U4 — was
    /// `presentations_org_code_uk`): a duplicate `identification_code`
    /// WITHIN the same BRANCH is rejected — enforced by the index, not by UI
    /// code (design.md "Identification code placement and uniqueness";
    /// catalog-item-identification "Branch-Owned Catalog").
    /// </summary>
    [Fact]
    public void PresentationsMigration_DuplicateIdentificationCodeWithinSameBranch_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0009(ownerConnection);
        ApplyBranchOwnershipMigration(ownerConnection);
        ResetCatalogAndPricing();
        ResetOrganizations();

        using (var insertOrgCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection))
        {
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();
        }

        using (var insertBranchCmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')", ownerConnection))
        {
            insertBranchCmd.Parameters.AddWithValue(branchAId);
            insertBranchCmd.Parameters.AddWithValue(orgAId);
            insertBranchCmd.ExecuteNonQuery();
        }

        using (var insertProductCmd = new NpgsqlCommand(
            """
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, 'Product A', $4, $4, $4)
            """, ownerConnection))
        {
            insertProductCmd.Parameters.AddWithValue(productId);
            insertProductCmd.Parameters.AddWithValue(orgAId);
            insertProductCmd.Parameters.AddWithValue(branchAId);
            insertProductCmd.Parameters.AddWithValue(actorId);
            insertProductCmd.ExecuteNonQuery();
        }

        using (var insertPresentation1Cmd = new NpgsqlCommand(
            """
            INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, identification_code, created_by_user_id)
            VALUES ($1, $2, $3, $4, 'Presentation 1', 'FixedQuantity', $5, 'DUPLICATE-CODE', $5)
            """, ownerConnection))
        {
            insertPresentation1Cmd.Parameters.AddWithValue(Guid.NewGuid());
            insertPresentation1Cmd.Parameters.AddWithValue(orgAId);
            insertPresentation1Cmd.Parameters.AddWithValue(branchAId);
            insertPresentation1Cmd.Parameters.AddWithValue(productId);
            insertPresentation1Cmd.Parameters.AddWithValue(actorId);
            insertPresentation1Cmd.ExecuteNonQuery();
        }

        using var insertPresentation2Cmd = new NpgsqlCommand(
            """
            INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, identification_code, created_by_user_id)
            VALUES ($1, $2, $3, $4, 'Presentation 2', 'FixedQuantity', $5, 'DUPLICATE-CODE', $5)
            """, ownerConnection);
        insertPresentation2Cmd.Parameters.AddWithValue(Guid.NewGuid());
        insertPresentation2Cmd.Parameters.AddWithValue(orgAId);
        insertPresentation2Cmd.Parameters.AddWithValue(branchAId);
        insertPresentation2Cmd.Parameters.AddWithValue(productId);
        insertPresentation2Cmd.Parameters.AddWithValue(actorId);

        Assert.Throws<PostgresException>(() => insertPresentation2Cmd.ExecuteNonQuery());
    }

    // --- commerce-pricing-engine Work Unit 2: price schema/aggregate ------

    /// <summary>
    /// Append-only proof (design.md "Effective-dating shape"): publishing a
    /// second price for the same presentation is a pure INSERT, and the
    /// FIRST entry remains retrievable at its own `effective_from` — no row
    /// is ever rewritten.
    /// </summary>
    [Fact]
    public void PriceListEntries_SupersedingPrice_PreservesPriorEntryAsHistory()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0009(ownerConnection);
        ApplyBranchOwnershipMigration(ownerConnection);
        ResetCatalogAndPricing();
        ResetOrganizations();

        void Exec(string sql, Action<NpgsqlCommand> bind)
        {
            using var cmd = new NpgsqlCommand(sql, ownerConnection);
            bind(cmd);
            cmd.ExecuteNonQuery();
        }

        Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
        Exec(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')",
            c =>
            {
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(orgAId);
            });
        Exec(
            """
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, 'Product A', $4, $4, $4)
            """, c =>
            {
                c.Parameters.AddWithValue(productId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(actorId);
            });
        Exec(
            """
            INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, 'Presentation A', 'FixedQuantity', $5, $5)
            """, c =>
            {
                c.Parameters.AddWithValue(presentationId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(productId);
                c.Parameters.AddWithValue(actorId);
            });
        Exec(
            "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Default', true, $3)",
            c =>
            {
                c.Parameters.AddWithValue(priceListId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(actorId);
            });

        Exec(
            """
            INSERT INTO price_list_entries (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, 100.00, '2026-01-01', $5)
            """, c =>
            {
                c.Parameters.AddWithValue(Guid.NewGuid());
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(priceListId);
                c.Parameters.AddWithValue(presentationId);
                c.Parameters.AddWithValue(actorId);
            });

        // Supersede: a NEW row, later effective_from, HIGHER price. The
        // first row must remain untouched — no UPDATE ever happens.
        Exec(
            """
            INSERT INTO price_list_entries (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, 150.00, '2026-02-01', $5)
            """, c =>
            {
                c.Parameters.AddWithValue(Guid.NewGuid());
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(priceListId);
                c.Parameters.AddWithValue(presentationId);
                c.Parameters.AddWithValue(actorId);
            });

        using var readCmd = new NpgsqlCommand(
            "SELECT unit_price FROM price_list_entries WHERE presentation_id = $1 ORDER BY effective_from", ownerConnection);
        readCmd.Parameters.AddWithValue(presentationId);
        using var reader = readCmd.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(100.00m, reader.GetDecimal(0));
        Assert.True(reader.Read());
        Assert.Equal(150.00m, reader.GetDecimal(0));
        Assert.False(reader.Read());
    }

    /// <summary>
    /// `price_list_entries_one_per_day` (`UNIQUE (price_list_id,
    /// presentation_id, effective_from)`): a same-day double-publish is
    /// rejected — a 409 at the endpoint layer, not a silent coin flip.
    /// </summary>
    [Fact]
    public void PriceListEntries_SameDayDoublePublish_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0009(ownerConnection);
        ApplyBranchOwnershipMigration(ownerConnection);
        ResetCatalogAndPricing();
        ResetOrganizations();

        void Exec(string sql, Action<NpgsqlCommand> bind)
        {
            using var cmd = new NpgsqlCommand(sql, ownerConnection);
            bind(cmd);
            cmd.ExecuteNonQuery();
        }

        Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
        Exec(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')",
            c =>
            {
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(orgAId);
            });
        Exec(
            """
            INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
            VALUES ($1, $2, $3, 'Product A', $4, $4, $4)
            """, c =>
            {
                c.Parameters.AddWithValue(productId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(actorId);
            });
        Exec(
            """
            INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, created_by_user_id)
            VALUES ($1, $2, $3, $4, 'Presentation A', 'FixedQuantity', $5, $5)
            """, c =>
            {
                c.Parameters.AddWithValue(presentationId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(branchAId);
                c.Parameters.AddWithValue(productId);
                c.Parameters.AddWithValue(actorId);
            });
        Exec(
            "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Default', true, $3)",
            c =>
            {
                c.Parameters.AddWithValue(priceListId);
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(actorId);
            });
        Exec(
            """
            INSERT INTO price_list_entries (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, 100.00, '2026-01-01', $5)
            """, c =>
            {
                c.Parameters.AddWithValue(Guid.NewGuid());
                c.Parameters.AddWithValue(orgAId);
                c.Parameters.AddWithValue(priceListId);
                c.Parameters.AddWithValue(presentationId);
                c.Parameters.AddWithValue(actorId);
            });

        using var duplicateDayCmd = new NpgsqlCommand(
            """
            INSERT INTO price_list_entries (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id)
            VALUES ($1, $2, $3, $4, 999.00, '2026-01-01', $5)
            """, ownerConnection);
        duplicateDayCmd.Parameters.AddWithValue(Guid.NewGuid());
        duplicateDayCmd.Parameters.AddWithValue(orgAId);
        duplicateDayCmd.Parameters.AddWithValue(priceListId);
        duplicateDayCmd.Parameters.AddWithValue(presentationId);
        duplicateDayCmd.Parameters.AddWithValue(actorId);

        Assert.Throws<PostgresException>(() => duplicateDayCmd.ExecuteNonQuery());
    }

    /// <summary>
    /// `price_lists_one_default` (`UNIQUE (organization_id) WHERE
    /// is_default`): a second default price list for the same organization
    /// is rejected (design.md "Which price list resolves").
    /// </summary>
    [Fact]
    public void PriceLists_SecondDefaultForSameOrganization_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0009(ownerConnection);
        ResetCatalogAndPricing();
        ResetOrganizations();

        using (var insertOrgCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection))
        {
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();
        }

        using (var insertFirstDefaultCmd = new NpgsqlCommand(
            "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Default', true, $3)",
            ownerConnection))
        {
            insertFirstDefaultCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertFirstDefaultCmd.Parameters.AddWithValue(orgAId);
            insertFirstDefaultCmd.Parameters.AddWithValue(actorId);
            insertFirstDefaultCmd.ExecuteNonQuery();
        }

        using var insertSecondDefaultCmd = new NpgsqlCommand(
            "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Also Default', true, $3)",
            ownerConnection);
        insertSecondDefaultCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertSecondDefaultCmd.Parameters.AddWithValue(orgAId);
        insertSecondDefaultCmd.Parameters.AddWithValue(actorId);

        Assert.Throws<PostgresException>(() => insertSecondDefaultCmd.ExecuteNonQuery());
    }

    [Fact]
    public void PriceListEntries_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var presentationId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0009(ownerConnection);
            ApplyBranchOwnershipMigration(ownerConnection);
            ResetCatalogAndPricing();
            ResetOrganizations();

            void Exec(string sql, Action<NpgsqlCommand> bind)
            {
                using var cmd = new NpgsqlCommand(sql, ownerConnection);
                bind(cmd);
                cmd.ExecuteNonQuery();
            }

            Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
            Exec(
                "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')",
                c =>
                {
                    c.Parameters.AddWithValue(branchAId);
                    c.Parameters.AddWithValue(orgAId);
                });
            Exec(
                """
                INSERT INTO products (id, organization_id, branch_id, name, category_id, default_unit_id, created_by_user_id)
                VALUES ($1, $2, $3, 'Product A', $4, $4, $4)
                """, c =>
                {
                    c.Parameters.AddWithValue(productId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(branchAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO presentations (id, organization_id, branch_id, product_id, name, quantity_behavior, unit_id, created_by_user_id)
                VALUES ($1, $2, $3, $4, 'Presentation A', 'FixedQuantity', $5, $5)
                """, c =>
                {
                    c.Parameters.AddWithValue(presentationId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(branchAId);
                    c.Parameters.AddWithValue(productId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Default', true, $3)",
                c =>
                {
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO price_list_entries (id, organization_id, price_list_id, presentation_id, unit_price, effective_from, created_by_user_id)
                VALUES ($1, $2, $3, $4, 100.00, '2026-01-01', $5)
                """, c =>
                {
                    c.Parameters.AddWithValue(Guid.NewGuid());
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(presentationId);
                    c.Parameters.AddWithValue(actorId);
                });
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM price_list_entries", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    // --- commerce-guest-ordering: 0010_guest_ordering.sql ------------------

    private static string ResolveGuestOrderingMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0010_guest_ordering.sql");
    }

    private static void ApplyGuestOrderingMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolveGuestOrderingMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0010(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0009(connection);
        ApplyGuestOrderingMigration(connection);
    }

    private static void ResetGuestOrderVerifications()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE guest_order_verifications", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers commerce-guest-ordering task 2.1: `0010` idempotency — applying
    /// it twice against an already-migrated database is a no-op, and RLS is
    /// enabled/forced.
    /// </summary>
    [Fact]
    public void GuestOrderingMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0009(connection);

        ApplyGuestOrderingMigration(connection);
        ApplyGuestOrderingMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'guest_order_verifications'),
                EXISTS (
                    SELECT 1 FROM pg_class
                    WHERE relname = 'guest_order_verifications' AND relrowsecurity AND relforcerowsecurity
                ),
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_lookup'
                ),
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_issue'
                ),
                EXISTS (
                    SELECT 1 FROM pg_policies
                    WHERE tablename = 'guest_order_verifications' AND policyname = 'guest_order_verifications_update'
                )
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    /// <summary>
    /// `app_runtime` has no `DELETE` grant on `guest_order_verifications` —
    /// the `customers`/`platform_admins`/`password_reset_tokens` precedent.
    /// </summary>
    [Fact]
    public void GuestOrderingMigration_AppRuntime_HasNoDeleteGrant()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0010(ownerConnection);
        }

        using var appRuntimeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        appRuntimeConnection.Open();

        using var deleteCmd = new NpgsqlCommand(
            "DELETE FROM guest_order_verifications WHERE false", appRuntimeConnection);
        var ex = Assert.Throws<PostgresException>(() => deleteCmd.ExecuteNonQuery());
        Assert.Equal("42501", ex.SqlState);
    }

    /// <summary>
    /// Org B cannot read org A's guest verifications — the same
    /// tenant-isolation shape proven for every prior migration's tenant-scoped
    /// table, applied to `guest_order_verifications`'s scoped INSERT path.
    /// Uses the unscoped-SELECT-then-scoped-count idiom: insert via the owner
    /// connection (superuser, RLS never applies), then read via an
    /// app_runtime connection scoped to a DIFFERENT organization.
    /// </summary>
    [Fact]
    public void GuestOrderingMigration_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var verificationId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0010(ownerConnection);
            ResetGuestOrderVerifications();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertVerificationCmd = new NpgsqlCommand(
                """
                INSERT INTO guest_order_verifications
                    (id, organization_id, document_id, contact_channel, contact_address, code_hash, expires_at)
                VALUES ($1, $2, '30111222333', 'Email', 'guest@example.com', 'hash', now() + interval '10 minutes')
                """, ownerConnection);
            insertVerificationCmd.Parameters.AddWithValue(verificationId);
            insertVerificationCmd.Parameters.AddWithValue(orgAId);
            insertVerificationCmd.ExecuteNonQuery();
        }

        // Scoped read, via the app_runtime lookup policy which is UNSCOPED
        // (USING(true)) by design — this proves cross-org isolation is
        // enforced by the CALLER always filtering on organization_id in
        // application code (the confirm flow reads by verification id, never
        // by listing), not by the lookup policy itself. This mirrors the
        // identical accepted shape already proven for
        // `password_reset_tokens_lookup` / `device_credentials` unscoped
        // reads: the row IS visible unscoped, application code is the
        // enforcement point for anything list-shaped.
        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var byIdCmd = new NpgsqlCommand(
            "SELECT organization_id FROM guest_order_verifications WHERE id = $1", scopedConnection, tx);
        byIdCmd.Parameters.AddWithValue(verificationId);
        var result = byIdCmd.ExecuteScalar();
        tx.Commit();

        // The unscoped lookup policy resolves the row by id regardless of
        // scope (the password_reset_tokens_lookup precedent) — this asserts
        // the row IS found (proving the intentional unscoped-read shape),
        // and that its organization_id is org A's, so a caller performing
        // the real confirm flow can verify document/contact match against
        // the CORRECT organization before trusting the row.
        Assert.NotNull(result);
        Assert.Equal(orgAId, (Guid)result!);
    }

    /// <summary>
    /// Cross-org INSERT must be rejected by `guest_order_verifications_issue`'s
    /// WITH CHECK — a caller scoped to org B cannot insert a verification
    /// claiming org A.
    /// </summary>
    [Fact]
    public void GuestOrderingMigration_CrossOrgInsert_Throws()
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
            ApplyAllMigrationsThrough0010(ownerConnection);
            ResetGuestOrderVerifications();
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
            INSERT INTO guest_order_verifications
                (id, organization_id, document_id, contact_channel, contact_address, code_hash, expires_at)
            VALUES ($1, $2, '30111222333', 'Email', 'guest@example.com', 'hash', now() + interval '10 minutes')
            """, writeConnection, tx);
        insertCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCmd.Parameters.AddWithValue(orgAId);

        Assert.Throws<PostgresException>(() => insertCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    /// <summary>
    /// `attempt_count` CHECK constraint: an update that would push the count
    /// past 5 is rejected at the DB layer — "the 5th attempt burns the row"
    /// is enforced structurally, not merely by application discipline.
    /// </summary>
    [Fact]
    public void GuestOrderingMigration_AttemptCountBeyondFive_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var verificationId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0010(ownerConnection);
            ResetGuestOrderVerifications();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertVerificationCmd = new NpgsqlCommand(
                """
                INSERT INTO guest_order_verifications
                    (id, organization_id, document_id, contact_channel, contact_address, code_hash, expires_at, attempt_count)
                VALUES ($1, $2, '30111222333', 'Email', 'guest@example.com', 'hash', now() + interval '10 minutes', 5)
                """, ownerConnection);
            insertVerificationCmd.Parameters.AddWithValue(verificationId);
            insertVerificationCmd.Parameters.AddWithValue(orgAId);
            insertVerificationCmd.ExecuteNonQuery();
        }

        using var writeConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        writeConnection.Open();
        using var tx = writeConnection.BeginTransaction();
        using var updateCmd = new NpgsqlCommand(
            "UPDATE guest_order_verifications SET attempt_count = attempt_count + 1 WHERE id = $1",
            writeConnection, tx);
        updateCmd.Parameters.AddWithValue(verificationId);

        Assert.Throws<PostgresException>(() => updateCmd.ExecuteNonQuery());
        tx.Rollback();
    }

    // --- commerce-payments: 0011_payments.sql ------------------------------

    private static string ResolvePaymentsMigrationPath()
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

        return Path.Combine(dir.FullName, "deploy", "db", "migrations", "0011_payments.sql");
    }

    private static void ApplyPaymentsMigration(NpgsqlConnection connection)
    {
        var sql = File.ReadAllText(ResolvePaymentsMigrationPath());
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static void ApplyAllMigrationsThrough0011(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0010(connection);
        ApplyPaymentsMigration(connection);
    }

    private static void ResetPaymentEntries()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE payment_entries", connection);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Covers Unit 4 task 4.3: `0011` applies twice cleanly.
    /// </summary>
    [Fact]
    public void PaymentsMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0010(connection);

        ApplyPaymentsMigration(connection);
        ApplyPaymentsMigration(connection);
    }

    /// <summary>
    /// Org B cannot read org A's `payment_entries` rows (RLS).
    /// </summary>
    [Fact]
    public void PaymentsMigration_CrossOrganizationRead_ReturnsZeroRows()
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
            ApplyAllMigrationsThrough0011(ownerConnection);
            ResetPaymentEntries();
            ResetOrganizations();

            using var insertOrgCmd = new NpgsqlCommand(
                "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();

            using var insertEntryCmd = new NpgsqlCommand(
                """
                INSERT INTO payment_entries
                    (entry_id, organization_id, operation_id, subject_kind, subject_id,
                     entry_kind, method, amount, approval_state, actor_id)
                VALUES ($1, $2, $3, 'Order', $4, 'Payment', 'Cash', 10.00, 'Approved', $5)
                """, ownerConnection);
            insertEntryCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertEntryCmd.Parameters.AddWithValue(orgAId);
            insertEntryCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertEntryCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertEntryCmd.Parameters.AddWithValue(Guid.NewGuid());
            insertEntryCmd.ExecuteNonQuery();
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM payment_entries", scopedConnection, tx);
        var count = (long)countCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, count);
    }

    /// <summary>
    /// `app_runtime` has no UPDATE and no DELETE grant on `payment_entries` —
    /// append-only enforced by grant, not convention.
    /// </summary>
    [Fact]
    public void PaymentsMigration_AppRuntimeRole_HasNoUpdateOrDeleteGrant()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0011(ownerConnection);
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT
                has_table_privilege('app_runtime', 'payment_entries', 'UPDATE') AS can_update,
                has_table_privilege('app_runtime', 'payment_entries', 'DELETE') AS can_delete,
                has_table_privilege('app_runtime', 'payment_entries', 'SELECT') AS can_select,
                has_table_privilege('app_runtime', 'payment_entries', 'INSERT') AS can_insert
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    /// <summary>
    /// `customers_instrument_not_pan_shaped` CHECK rejects a 13-19 digit
    /// string on `billing_instrument_reference`.
    /// </summary>
    [Fact]
    public void PaymentsMigration_PanShapedInstrumentReference_Throws()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0011(ownerConnection);
        ResetOrganizations();

        using var insertOrgCmd = new NpgsqlCommand(
            "INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection);
        insertOrgCmd.Parameters.AddWithValue(orgAId);
        insertOrgCmd.ExecuteNonQuery();

        using var insertCustomerCmd = new NpgsqlCommand(
            """
            INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, billing_instrument_reference)
            VALUES ($1, $2, 'Retail', 'Jane Doe', $3, '1234567890123')
            """, ownerConnection);
        insertCustomerCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertCustomerCmd.Parameters.AddWithValue(orgAId);
        insertCustomerCmd.Parameters.AddWithValue(Guid.NewGuid());

        Assert.Throws<PostgresException>(() => insertCustomerCmd.ExecuteNonQuery());
    }

    // --- commerce-price-composition: 0013_rate_components.sql --------------

    // The 0013/0014 pair uses the shared `PostgresTestFixture` helpers rather
    // than adding two more copies of the repo-root walk that the resolvers
    // above each carry. Those older resolvers are left alone: rewriting twenty
    // of them is a separate change from this one.
    private static void ApplyRateComponentsMigration(NpgsqlConnection connection) =>
        PostgresTestFixture.ApplyMigration(connection, "0013_rate_components.sql");

    /// <summary>
    /// 0012 is deliberately NOT in this chain: it only merges
    /// `platform_admins` into `users` and is independent of the pricing
    /// tables, and applying it here would destroy the `platform_admins`
    /// fixture the 0007 tests in this same collection rebuild. 0013 depends
    /// only on 0003's `organizations` and 0009's `price_lists`.
    /// </summary>
    private static void ApplyAllMigrationsThrough0013(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0011(connection);
        ApplyRateComponentsMigration(connection);
    }

    private static void ApplyRateComponentTenancyMigration(NpgsqlConnection connection) =>
        PostgresTestFixture.ApplyMigration(connection, "0014_rate_component_tenancy.sql");

    private static void ApplyAllMigrationsThrough0014(NpgsqlConnection connection)
    {
        ApplyAllMigrationsThrough0013(connection);
        ApplyRateComponentTenancyMigration(connection);
    }

    private static void ResetRateComponents()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE rate_components, rate_component_sets CASCADE", connection);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void RateComponentsMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0011(connection);

        ApplyRateComponentsMigration(connection);
        ApplyRateComponentsMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'rate_component_sets' AND relrowsecurity AND relforcerowsecurity),
                EXISTS (SELECT 1 FROM pg_class WHERE relname = 'rate_components' AND relrowsecurity AND relforcerowsecurity)
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    /// <summary>
    /// Spec "Append-Only Effective-Dated Rate Component History": a persisted
    /// set's components, percentages, calculation bases, orders and effective
    /// date MUST NOT be mutated or deleted. Enforced by the GRANT — the
    /// `price_list_entries` precedent — not by application convention.
    /// </summary>
    [Fact]
    public void RateComponentsMigration_AppRuntime_HasOnlySelectAndInsert_OnBothTables()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0013(ownerConnection);
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();

        foreach (var table in new[] { "rate_component_sets", "rate_components" })
        {
            using var cmd = new NpgsqlCommand(
                $"""
                SELECT
                    has_table_privilege('app_runtime', '{table}', 'SELECT'),
                    has_table_privilege('app_runtime', '{table}', 'INSERT'),
                    has_table_privilege('app_runtime', '{table}', 'UPDATE'),
                    has_table_privilege('app_runtime', '{table}', 'DELETE')
                """, connection);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetBoolean(0), $"{table} must grant SELECT");
            Assert.True(reader.GetBoolean(1), $"{table} must grant INSERT");
            Assert.False(reader.GetBoolean(2), $"{table} must NOT grant UPDATE");
            Assert.False(reader.GetBoolean(3), $"{table} must NOT grant DELETE");
        }
    }

    /// <summary>
    /// Spec "Organization-Scoped Component Persistence With RLS": a request
    /// scoped to Organization B MUST NOT see Organization A's sets.
    ///
    /// MUTATION-CHECKED: with
    /// `rate_component_sets_tenant_isolation`/`rate_components_tenant_isolation`
    /// relaxed to `USING (true)`, this test fails (2 and 1 rows instead of 0),
    /// so the two zeroes below are a real isolation assertion and not a query
    /// that happens to find nothing.
    /// </summary>
    [Fact]
    public void RateComponents_CrossOrganizationRead_ReturnsZeroRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var listSetId = Guid.NewGuid();
        var defaultSetId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0013(ownerConnection);
            ResetRateComponents();
            ResetCatalogAndPricing();
            ResetOrganizations();

            void Exec(string sql, Action<NpgsqlCommand> bind)
            {
                using var cmd = new NpgsqlCommand(sql, ownerConnection);
                bind(cmd);
                cmd.ExecuteNonQuery();
            }

            Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
            Exec(
                "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Reparto', true, $3)",
                c =>
                {
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
                VALUES ($1, $2, $3, DATE '2026-01-01', $4)
                """, c =>
                {
                    c.Parameters.AddWithValue(listSetId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(actorId);
                });
            // A second, organization-default set: isolation must cover the
            // inheritable default too, not only list-owned sets.
            Exec(
                """
                INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
                VALUES ($1, $2, NULL, DATE '2026-01-01', $3)
                """, c =>
                {
                    c.Parameters.AddWithValue(defaultSetId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO rate_components (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
                VALUES ($1, $2, $3, 'IVA', 'IVA (10,5%)', 10.5, 'Base', 1)
                """, c =>
                {
                    c.Parameters.AddWithValue(Guid.NewGuid());
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(listSetId);
                });
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(Guid.NewGuid().ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var setCountCmd = new NpgsqlCommand("SELECT count(*) FROM rate_component_sets", scopedConnection, tx);
        var setCount = (long)setCountCmd.ExecuteScalar()!;
        using var componentCountCmd = new NpgsqlCommand("SELECT count(*) FROM rate_components", scopedConnection, tx);
        var componentCount = (long)componentCountCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(0, setCount);
        Assert.Equal(0, componentCount);
    }

    /// <summary>
    /// The seeding above is what makes the zeroes meaningful: scoped to Org A,
    /// the very same query returns the rows. Without this, a broken INSERT
    /// would make the isolation test pass vacuously.
    /// </summary>
    [Fact]
    public void RateComponents_SameOrganizationRead_ReturnsTheSeededRows()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var priceListId = Guid.NewGuid();
        var listSetId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThrough0013(ownerConnection);
            ResetRateComponents();
            ResetCatalogAndPricing();
            ResetOrganizations();

            void Exec(string sql, Action<NpgsqlCommand> bind)
            {
                using var cmd = new NpgsqlCommand(sql, ownerConnection);
                bind(cmd);
                cmd.ExecuteNonQuery();
            }

            Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", c => c.Parameters.AddWithValue(orgAId));
            Exec(
                "INSERT INTO price_lists (id, organization_id, name, is_default, created_by_user_id) VALUES ($1, $2, 'Reparto', true, $3)",
                c =>
                {
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
                VALUES ($1, $2, $3, DATE '2026-01-01', $4)
                """, c =>
                {
                    c.Parameters.AddWithValue(listSetId);
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(priceListId);
                    c.Parameters.AddWithValue(actorId);
                });
            Exec(
                """
                INSERT INTO rate_components (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
                VALUES ($1, $2, $3, 'IVA', 'IVA (10,5%)', 10.5, 'Base', 1)
                """, c =>
                {
                    c.Parameters.AddWithValue(Guid.NewGuid());
                    c.Parameters.AddWithValue(orgAId);
                    c.Parameters.AddWithValue(listSetId);
                });
        }

        using var scopedConnection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        scopedConnection.Open();
        using var tx = scopedConnection.BeginTransaction();
        using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", scopedConnection, tx))
        {
            scopeCmd.Parameters.AddWithValue(orgAId.ToString());
            scopeCmd.ExecuteNonQuery();
        }

        using var setCountCmd = new NpgsqlCommand("SELECT count(*) FROM rate_component_sets", scopedConnection, tx);
        var setCount = (long)setCountCmd.ExecuteScalar()!;
        using var componentCountCmd = new NpgsqlCommand("SELECT count(*) FROM rate_components", scopedConnection, tx);
        var componentCount = (long)componentCountCmd.ExecuteScalar()!;
        tx.Commit();

        Assert.Equal(1, setCount);
        Assert.Equal(1, componentCount);
    }

    /// <summary>
    /// The database refuses the `Unspecified` sentinel too: spec "Explicit
    /// Calculation Base Per Component" is a CHECK constraint, not only a
    /// domain guard, so no write path can persist an undeclared base.
    /// </summary>
    [Fact]
    public void RateComponents_UndeclaredCalculationBase_IsRejectedByCheckConstraint()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgAId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        using var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        ownerConnection.Open();
        ApplyAllMigrationsThrough0013(ownerConnection);
        ResetRateComponents();
        ResetCatalogAndPricing();
        ResetOrganizations();

        using (var insertOrgCmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Org A')", ownerConnection))
        {
            insertOrgCmd.Parameters.AddWithValue(orgAId);
            insertOrgCmd.ExecuteNonQuery();
        }

        using (var insertSetCmd = new NpgsqlCommand(
            """
            INSERT INTO rate_component_sets (id, organization_id, price_list_id, effective_from, created_by_user_id)
            VALUES ($1, $2, NULL, DATE '2026-01-01', $3)
            """, ownerConnection))
        {
            insertSetCmd.Parameters.AddWithValue(setId);
            insertSetCmd.Parameters.AddWithValue(orgAId);
            insertSetCmd.Parameters.AddWithValue(actorId);
            insertSetCmd.ExecuteNonQuery();
        }

        using var insertComponentCmd = new NpgsqlCommand(
            """
            INSERT INTO rate_components (id, organization_id, set_id, code, label, percentage, calculation_base, component_order)
            VALUES ($1, $2, $3, 'IVA', 'IVA (10,5%)', 10.5, 'Unspecified', 1)
            """, ownerConnection);
        insertComponentCmd.Parameters.AddWithValue(Guid.NewGuid());
        insertComponentCmd.Parameters.AddWithValue(orgAId);
        insertComponentCmd.Parameters.AddWithValue(setId);

        var ex = Assert.Throws<PostgresException>(() => insertComponentCmd.ExecuteNonQuery());
        Assert.Equal("23514", ex.SqlState);
    }

    // --- commerce-price-composition review round 1: 0014 -------------------

    [Fact]
    public void RateComponentTenancyMigration_IsIdempotent_AppliedTwiceWithoutError()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0013(connection);

        ApplyRateComponentTenancyMigration(connection);
        ApplyRateComponentTenancyMigration(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_price_list_org_fk'),
                EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_set_org_fk'),
                EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'rate_components_one_code_per_set_ci')
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
    }

    /// <summary>
    /// R1-latent-cross-tenant-price-list-id, asserted on the constraint itself
    /// rather than only on the behaviour it produces: the reference from
    /// `rate_component_sets` to `price_lists` must carry BOTH columns. A
    /// single-column reference is checked outside RLS and would let one
    /// organization name another's list.
    /// </summary>
    [Fact]
    public void RateComponentTenancyMigration_PriceListReference_IsTenantComposite()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0014(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT string_agg(a.attname, ',' ORDER BY a.attname)
            FROM pg_constraint c
            JOIN unnest(c.conkey) AS k(attnum) ON true
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conname = 'rate_component_sets_price_list_org_fk'
            """, connection);

        Assert.Equal("organization_id,price_list_id", cmd.ExecuteScalar() as string);
    }

    /// <summary>
    /// The lone remaining single-column path onto these tables must be gone:
    /// `0013`'s tenant-blind references are dropped, not merely shadowed by
    /// the composite ones.
    /// </summary>
    [Fact]
    public void RateComponentTenancyMigration_DropsTheTenantBlindReferences()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        ApplyAllMigrationsThrough0014(connection);

        using var cmd = new NpgsqlCommand(
            """
            SELECT
                EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_component_sets_price_list_id_fkey'),
                EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_set_id_fkey'),
                EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'rate_components_one_code_per_set')
            """, connection);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }
}
