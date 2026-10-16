using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0022_terminal_registers.sql` (pos-installation-identity "Register Number"):
/// backfill from live device credentials per branch in issue order, a safe
/// re-run, the allocation function (sequence, reuse, release on another branch,
/// numbers never reused) and the table constraints. Runs in a throwaway
/// database so it never disturbs the shared `commerce_test` schema.
/// </summary>
[Collection("Postgres")]
public sealed class TerminalRegistersMigrationTests
{
    private const string MigrationFile = "0022_terminal_registers.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string Scratch(string dbName) =>
        new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)cmd.ExecuteScalar()!;
    }

    private static void SeedOrg(NpgsqlConnection conn, Guid org) =>
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "Org " + org);

    private static void SeedBranch(NpgsqlConnection conn, Guid org, Guid id) =>
        Exec(conn, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", id, org, "Branch " + id);

    private static void SeedCredential(NpgsqlConnection conn, Guid org, Guid branch, Guid installation, DateTimeOffset issuedAt, bool revoked = false) =>
        Exec(conn,
            "INSERT INTO device_credentials (token_hash, id, organization_id, branch_id, installation_id, issued_to_user_id, is_revoked, issued_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)",
            Guid.NewGuid().ToString("N"), Guid.NewGuid(), org, branch, installation, Guid.NewGuid(), revoked, issuedAt);

    private static short Assign(NpgsqlConnection conn, Guid org, Guid branch, Guid installation)
    {
        Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
        return Scalar<short>(conn, "SELECT terminal_registers_assign($1, $2, $3)", org, branch, installation);
    }

    private static void WithScratchDatabase(bool applyMigration, Action<NpgsqlConnection> body)
    {
        var dbName = "tr0022_" + Guid.NewGuid().ToString("N");
        using (var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            admin.Open();
            Exec(admin, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        }
        try
        {
            using var conn = new NpgsqlConnection(Scratch(dbName));
            conn.Open();
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>()
                         .Where(f => string.CompareOrdinal(f, MigrationFile) < 0).OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(conn, file);
            }
            if (applyMigration) PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            body(conn);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            using var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            admin.Open();
            Exec(admin, $"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
        }
    }

    [Fact]
    public void Backfill_NumbersLiveInstallationsPerBranchInIssueOrder_SkipsRevoked_AndIsIdempotent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: false, conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, org);
            SeedBranch(conn, org, branchA);
            SeedBranch(conn, org, branchB);
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var (late, early, revoked, otherBranch) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            // Inserted out of issue order; one revoked row must not get a number.
            SeedCredential(conn, org, branchA, late, t0.AddDays(9));
            SeedCredential(conn, org, branchA, early, t0);
            SeedCredential(conn, org, branchA, revoked, t0.AddDays(1), revoked: true);
            SeedCredential(conn, org, branchB, otherBranch, t0.AddDays(2));
            // A re-paired installation: its older credential is revoked, the newer one is live.
            SeedCredential(conn, org, branchA, early, t0.AddDays(-3), revoked: true);

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            short NumberOf(Guid installation) =>
                Scalar<short>(conn, "SELECT register_number FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", installation);
            Assert.Equal((short)1, NumberOf(early));
            Assert.Equal((short)2, NumberOf(late));
            Assert.Equal((short)1, NumberOf(otherBranch));
            Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1", revoked));

            const string Snapshot = "SELECT string_agg(installation_id::text || ':' || register_number, ',' ORDER BY installation_id) FROM terminal_registers";
            var before = Scalar<string>(conn, Snapshot);
            PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            Assert.Equal(before, Scalar<string>(conn, Snapshot));
            Assert.True(Scalar<bool>(conn, "SELECT relforcerowsecurity FROM pg_class WHERE relname = 'terminal_registers'"),
                "FORCE RLS must be restored");
        });
    }

    [Fact]
    public void Assign_AllocatesSequentially_ReusesOnRepairInTheSameBranch_AndNumbersPerBranchIndependently()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, org);
            SeedBranch(conn, org, branchA);
            SeedBranch(conn, org, branchB);
            var (first, second, third) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            Assert.Equal((short)1, Assign(conn, org, branchA, first));
            Assert.Equal((short)2, Assign(conn, org, branchA, second));
            Assert.Equal((short)1, Assign(conn, org, branchB, third));
            Assert.Equal((short)1, Assign(conn, org, branchA, first));
            Assert.Equal((short)2, Assign(conn, org, branchA, second));
            Assert.Equal(3L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers"));
        });
    }

    [Fact]
    public void Assign_ToAnotherBranch_ReleasesTheOldSlot_AndNeverReusesAFreedNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, org);
            SeedBranch(conn, org, branchA);
            SeedBranch(conn, org, branchB);
            var (mover, stayer, newcomer) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal((short)1, Assign(conn, org, branchA, mover));
            Assert.Equal((short)2, Assign(conn, org, branchA, stayer));

            // Moving to branch B frees register 1 of branch A, but never hands it out again.
            Assert.Equal((short)1, Assign(conn, org, branchB, mover));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", mover));
            Assert.Equal((short)3, Assign(conn, org, branchA, newcomer));

            // Coming back to branch A gives the mover its OWN old number, not a new one.
            Assert.Equal((short)1, Assign(conn, org, branchA, mover));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", mover));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1 AND branch_id = $2 AND released_at IS NOT NULL", mover, branchB));
        });
    }

    [Fact]
    public void AssignWithoutRelease_ReturnsNull_WhenNoLiveCredentialBindsTheInstallationToTheBranch_AndWritesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, org);
            SeedBranch(conn, org, branchA);
            SeedBranch(conn, org, branchB);
            var installation = Guid.NewGuid();
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            // The re-pairing already happened: the branch-B credential is revoked, branch A is live.
            SeedCredential(conn, org, branchB, installation, t0, revoked: true);
            SeedCredential(conn, org, branchA, installation, t0.AddDays(1));
            Assert.Equal((short)1, Assign(conn, org, branchA, installation));

            // A late identity refresh for the OLD branch must neither allocate there nor release A.
            Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
            Assert.True(Scalar<object>(conn, "SELECT terminal_registers_assign($1, $2, $3, false)", org, branchB, installation) is DBNull);
            Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE branch_id = $1", branchB));
            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", installation));

            // For the branch its live credential names, the refresh is a plain idempotent read.
            Assert.Equal((short)1, Scalar<short>(conn, "SELECT terminal_registers_assign($1, $2, $3, false)", org, branchA, installation));
        });
    }

    [Fact]
    public void Assign_IntoAnotherOrganization_ReleasesTheRowOfTheFormerOne()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, orgA); SeedOrg(conn, orgB);
            SeedBranch(conn, orgA, branchA); SeedBranch(conn, orgB, branchB);
            var installation = Guid.NewGuid();
            Assert.Equal((short)1, Assign(conn, orgA, branchA, installation));

            Assert.Equal((short)1, Assign(conn, orgB, branchB, installation));

            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", installation));
            Assert.Equal(branchB, Scalar<Guid>(conn, "SELECT branch_id FROM terminal_registers WHERE installation_id = $1 AND released_at IS NULL", installation));
        });
    }

    [Fact]
    public void Table_RejectsOutOfRangeNumbers_DuplicateLiveInstallations_AndIdentityRewrites()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, org); SeedBranch(conn, org, branchA); SeedBranch(conn, org, branchB);
            Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
            var installation = Guid.NewGuid();
            Assert.Equal((short)1, Assign(conn, org, branchA, installation));

            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number) VALUES ($1,$2,$3,0)", org, branchA, Guid.NewGuid()));
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number) VALUES ($1,$2,$3,1000)", org, branchA, Guid.NewGuid()));
            // The same installation cannot hold a second LIVE register elsewhere.
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number) VALUES ($1,$2,$3,1)", org, branchB, installation));
            // A (branch, number) pair names one installation only.
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number) VALUES ($1,$2,$3,1)", org, branchA, Guid.NewGuid()));
            // Identity columns never change.
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "UPDATE terminal_registers SET register_number = 7 WHERE installation_id = $1", installation));
        });
    }

    [Fact]
    public void Assign_PastNumber999_FailsTheRangeCheck()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid(); var branch = Guid.NewGuid();
            SeedOrg(conn, org); SeedBranch(conn, org, branch);
            Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
            Exec(conn, "INSERT INTO terminal_registers (organization_id, branch_id, installation_id, register_number) VALUES ($1,$2,$3,999)", org, branch, Guid.NewGuid());

            var ex = Assert.Throws<PostgresException>(() => Assign(conn, org, branch, Guid.NewGuid()));
            Assert.Equal("terminal_registers_number_ck", ex.ConstraintName);
        });
    }
}
