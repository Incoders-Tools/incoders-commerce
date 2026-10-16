using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0021_branch_codes.sql` (organization-persistence "Branch Short Code"):
/// per-organization backfill in `created_at, id` order, NOT NULL + range +
/// uniqueness, server-side allocation for inserts that omit the code,
/// immutability, and a safe re-run. Runs in a throwaway database so it never
/// disturbs the shared `commerce_test` schema.
/// </summary>
[Collection("Postgres")]
public sealed class BranchCodesMigrationTests
{
    private const string MigrationFile = "0021_branch_codes.sql";
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

    private static void SeedBranch(NpgsqlConnection conn, Guid org, Guid id, string name, DateTimeOffset createdAt) =>
        Exec(conn, "INSERT INTO branches (id, organization_id, name, created_at) VALUES ($1, $2, $3, $4)", id, org, name, createdAt);

    private static void WithScratchDatabase(bool applyMigration, Action<NpgsqlConnection> body)
    {
        var dbName = "bc0021_" + Guid.NewGuid().ToString("N");
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
    public void Backfill_NumbersExistingBranchesPerOrganization_ByCreatedAtThenId_AndIsIdempotent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: false, conn =>
        {
            var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, orgA);
            SeedOrg(conn, orgB);
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            // Inserted out of creation order; two branches share a timestamp to exercise the id tie-break.
            var aLate = Guid.NewGuid();
            var aTieLow = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
            var aTieHigh = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
            var aFirst = Guid.NewGuid();
            SeedBranch(conn, orgA, aLate, "late", t0.AddDays(10));
            SeedBranch(conn, orgA, aTieHigh, "tie-high", t0.AddDays(5));
            SeedBranch(conn, orgA, aTieLow, "tie-low", t0.AddDays(5));
            SeedBranch(conn, orgA, aFirst, "first", t0);
            var bOnly = Guid.NewGuid();
            SeedBranch(conn, orgB, bOnly, "only", t0.AddDays(3));

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            short CodeOf(Guid id) => Scalar<short>(conn, "SELECT code FROM branches WHERE id = $1", id);
            Assert.Equal((short)1, CodeOf(aFirst));
            Assert.Equal((short)2, CodeOf(aTieLow));
            Assert.Equal((short)3, CodeOf(aTieHigh));
            Assert.Equal((short)4, CodeOf(aLate));
            Assert.Equal((short)1, CodeOf(bOnly));

            const string Snapshot = "SELECT string_agg(id::text || ':' || code, ',' ORDER BY id) FROM branches";
            var before = Scalar<string>(conn, Snapshot);
            PostgresTestFixture.ApplyMigration(conn, MigrationFile);
            Assert.Equal(before, Scalar<string>(conn, Snapshot));
            Assert.True(Scalar<bool>(conn, "SELECT relforcerowsecurity FROM pg_class WHERE relname = 'branches'"),
                "FORCE RLS must be restored");
        });
    }

    [Fact]
    public void Insert_WithoutCode_IsAllocatedSequentiallyPerOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var (orgA, orgB) = (Guid.NewGuid(), Guid.NewGuid());
            SeedOrg(conn, orgA);
            SeedOrg(conn, orgB);
            var t = DateTimeOffset.UtcNow;
            var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
            for (var i = 0; i < 3; i++) SeedBranch(conn, orgA, ids[i], "a" + i, t);
            var other = Guid.NewGuid();
            SeedBranch(conn, orgB, other, "b0", t);

            for (var i = 0; i < 3; i++)
                Assert.Equal((short)(i + 1), Scalar<short>(conn, "SELECT code FROM branches WHERE id = $1", ids[i]));
            Assert.Equal((short)1, Scalar<short>(conn, "SELECT code FROM branches WHERE id = $1", other));
        });
    }

    [Fact]
    public void Code_IsImmutable_AndConstrainedToRangeAndUniquePerOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(applyMigration: true, conn =>
        {
            var org = Guid.NewGuid();
            SeedOrg(conn, org);
            var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
            SeedBranch(conn, org, first, "first", DateTimeOffset.UtcNow);
            SeedBranch(conn, org, second, "second", DateTimeOffset.UtcNow);

            // Changing the code is rejected, even to an unused value.
            Assert.Throws<PostgresException>(() => Exec(conn, "UPDATE branches SET code = 50 WHERE id = $1", first));
            // Updating other columns keeps working.
            Exec(conn, "UPDATE branches SET name = 'renamed' WHERE id = $1", first);
            Assert.Equal((short)1, Scalar<short>(conn, "SELECT code FROM branches WHERE id = $1", first));

            // Range and uniqueness on explicit inserts.
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO branches (id, organization_id, name, code) VALUES ($1, $2, 'zero', 0)", Guid.NewGuid(), org));
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO branches (id, organization_id, name, code) VALUES ($1, $2, 'big', 1000)", Guid.NewGuid(), org));
            Assert.Throws<PostgresException>(() =>
                Exec(conn, "INSERT INTO branches (id, organization_id, name, code) VALUES ($1, $2, 'dup', 2)", Guid.NewGuid(), org));
        });
    }
}
