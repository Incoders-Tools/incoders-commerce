using Commerce.Cloud.Api.Auditing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy task 2.8: <see cref="AuditLogWriter"/>
/// writes exactly one row inside the CALLER-owned transaction, never calls
/// `Commit`/`Rollback`/`set_config` itself, and a subsequent rollback of the
/// outer transaction leaves no row (design.md "Audit write mechanism").
/// </summary>
[Collection("Postgres")]
public sealed class AuditLogWriterTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static void ApplyAllMigrationsThroughPlatformAdministration(NpgsqlConnection connection)
    {
        // Mirrors MigrationRlsTests' private chain — duplicated here rather
        // than exposed publicly, since these are two independently-owned
        // test classes and neither should reach into the other's privates.
        foreach (var fileName in new[]
                 {
                     "0001_init_rls.sql",
                     "0002_users.sql",
                     "0003_organizations_branches.sql",
                     "0004_device_credentials.sql",
                     "0005_password_recovery.sql",
                     "0006_role_taxonomy.sql",
                     "0007_platform_administration.sql",
                 })
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException("Could not locate repo root (Commerce.sln).");
            }

            var path = Path.Combine(dir.FullName, "deploy", "db", "migrations", fileName);
            var sql = File.ReadAllText(path)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.ExecuteNonQuery();
        }
    }

    private static void ResetAuditLog()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("TRUNCATE TABLE audit_log RESTART IDENTITY", connection);
        cmd.ExecuteNonQuery();
    }

    private static long CountAuditRows(Guid actorId)
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE actor_id = $1", connection);
        cmd.Parameters.AddWithValue(actorId);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task InsertAsync_WritesExactlyOneRow_InsideCallerOwnedTransaction_AndCommitsWithIt()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThroughPlatformAdministration(ownerConnection);
        }
        ResetAuditLog();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx))
            {
                scopeCmd.Parameters.AddWithValue(orgId.ToString());
                await scopeCmd.ExecuteNonQueryAsync();
            }

            await AuditLogWriter.InsertAsync(
                connection, tx,
                new UserManagementAuditEntry("org-user", actorId, orgId, "user", entityId, "user.created", null, "{}"),
                CancellationToken.None);

            // Not yet committed: a fresh connection must see nothing.
            Assert.Equal(0, CountAuditRows(actorId));

            await tx.CommitAsync();
        }

        Assert.Equal(1, CountAuditRows(actorId));
    }

    [Fact]
    public async Task InsertAsync_RollbackOfOuterTransaction_LeavesNoRow()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThroughPlatformAdministration(ownerConnection);
        }
        ResetAuditLog();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx))
            {
                scopeCmd.Parameters.AddWithValue(orgId.ToString());
                await scopeCmd.ExecuteNonQueryAsync();
            }

            await AuditLogWriter.InsertAsync(
                connection, tx,
                new UserManagementAuditEntry("org-user", actorId, orgId, "user", entityId, "user.created", null, "{}"),
                CancellationToken.None);

            await tx.RollbackAsync();
        }

        Assert.Equal(0, CountAuditRows(actorId));
    }

    /// <summary>
    /// Proves <see cref="AuditLogWriter.InsertAsync"/> never calls
    /// `set_config` itself: with NO scope set by the caller, an org-scoped
    /// entry (non-null `OrganizationId`) is rejected by `audit_log_append`'s
    /// `WITH CHECK`, exactly as if the caller had forgotten to scope its own
    /// transaction.
    /// </summary>
    [Fact]
    public async Task InsertAsync_NeverSetsConfigItself_UnscopedOrgEntryIsRejectedByPolicy()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        using (var ownerConnection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            ownerConnection.Open();
            ApplyAllMigrationsThroughPlatformAdministration(ownerConnection);
        }
        ResetAuditLog();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        // Deliberately NOT calling set_config before this.
        await Assert.ThrowsAsync<PostgresException>(() => AuditLogWriter.InsertAsync(
            connection, tx,
            new UserManagementAuditEntry("org-user", actorId, orgId, "user", entityId, "user.created", null, "{}"),
            CancellationToken.None));

        await tx.RollbackAsync();
    }
}
