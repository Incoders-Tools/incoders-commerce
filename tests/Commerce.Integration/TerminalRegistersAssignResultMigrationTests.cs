using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0024_terminal_registers_assign_result.sql`: terminal_registers_assign() reports whether it
/// ALLOCATED a brand-new number (`newly_allocated`), so the API audits an allocation without a
/// second query. Runs in a throwaway database on top of every earlier migration.
/// </summary>
[Collection("Postgres")]
public sealed class TerminalRegistersAssignResultMigrationTests
{
    private const string MigrationFile = "0024_terminal_registers_assign_result.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static void Exec(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static void WithScratchDatabase(Action<NpgsqlConnection> body)
    {
        var dbName = "tr0024_" + Guid.NewGuid().ToString("N");
        using (var admin = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            admin.Open();
            Exec(admin, $"CREATE DATABASE {dbName} OWNER commerce_owner");
        }
        try
        {
            var scratch = new NpgsqlConnectionStringBuilder(PostgresTestFixture.OwnerConnectionString) { Database = dbName }.ConnectionString;
            using var conn = new NpgsqlConnection(scratch);
            conn.Open();
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>()
                         .Where(f => string.CompareOrdinal(f, MigrationFile) <= 0).OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(conn, file);
            }
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

    private static (short? Number, bool Allocated) Assign(NpgsqlConnection conn, Guid org, Guid branch, Guid installation, bool releaseOthers = true)
    {
        Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
        using var cmd = new NpgsqlCommand("SELECT assigned_number, newly_allocated FROM terminal_registers_assign($1, $2, $3, $4)", conn);
        cmd.Parameters.AddWithValue(org);
        cmd.Parameters.AddWithValue(branch);
        cmd.Parameters.AddWithValue(installation);
        cmd.Parameters.AddWithValue(releaseOthers);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetInt16(0), reader.GetBoolean(1));
    }

    private static void Seed(NpgsqlConnection conn, Guid org, params Guid[] branches)
    {
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "Org " + org);
        foreach (var branch in branches)
        {
            Exec(conn, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", branch, org, "Branch " + branch);
        }
    }

    [Fact]
    public void ANewInstallation_IsReportedAsAllocated_AndARepeatOrAReactivationIsNot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var org = Guid.NewGuid();
            var (branchA, branchB) = (Guid.NewGuid(), Guid.NewGuid());
            Seed(conn, org, branchA, branchB);
            var installation = Guid.NewGuid();

            Assert.Equal(((short?)1, true), Assign(conn, org, branchA, installation));
            Assert.Equal(((short?)1, false), Assign(conn, org, branchA, installation));

            // Moving to branch B releases A's row; coming back re-activates the same number: not new.
            Assert.Equal(((short?)1, true), Assign(conn, org, branchB, installation));
            Assert.Equal(((short?)1, false), Assign(conn, org, branchA, installation));
        });
    }

    [Fact]
    public void AnIdentityRefreshWithoutALiveCredential_ReturnsNoNumber_AndAllocatesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var org = Guid.NewGuid();
            var branch = Guid.NewGuid();
            Seed(conn, org, branch);

            Assert.Equal(((short?)null, false), Assign(conn, org, branch, Guid.NewGuid(), releaseOthers: false));
        });
    }

    [Fact]
    public void TheMigration_CanBeAppliedTwice()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            var org = Guid.NewGuid();
            var branch = Guid.NewGuid();
            Seed(conn, org, branch);
            Assert.Equal(((short?)1, true), Assign(conn, org, branch, Guid.NewGuid()));
        });
    }
}
