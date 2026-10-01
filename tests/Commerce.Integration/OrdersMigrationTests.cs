using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0025_orders.sql`: web orders live in Postgres (`orders`, `order_lines`) and carry the human
/// number `P{branch}-W-{sequence}` as (branch_code, sequence). Runs in a throwaway database on top
/// of every earlier migration so the schema contract is proven independently of the store.
/// </summary>
[Collection("Postgres")]
public sealed class OrdersMigrationTests
{
    private const string MigrationFile = "0025_orders.sql";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

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

    private static void WithScratchDatabase(Action<NpgsqlConnection> body)
    {
        var dbName = "or0025_" + Guid.NewGuid().ToString("N");
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

    private static void AssertViolation(string sqlState, Action action) =>
        Assert.Equal(sqlState, Assert.Throws<PostgresException>(action).SqlState);

    private static (Guid Org, Guid Branch) SeedOrgAndBranch(NpgsqlConnection conn)
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, 'Org')", org);
        Exec(conn, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branch, org);
        return (org, branch);
    }

    private static void InsertOrder(NpgsqlConnection conn, Guid org, Guid branch, Guid orderId, int sequence,
        Guid? customerId = null, string origin = "RegisteredCustomer", string? guestDocument = null, short branchCode = 1)
    {
        Exec(conn,
            """
            INSERT INTO orders (organization_id, order_id, destination_branch_id, origin, customer_id, guest_document_id,
                                status, pending_reason, branch_code, sequence, submitted_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, 'PendingDestination', 'DestinationOffline', $7, $8, now())
            """,
            org, orderId, branch, origin,
            (object?)customerId ?? DBNull.Value, (object?)guestDocument ?? DBNull.Value, branchCode, sequence);
    }

    [Fact]
    public void BothTables_AreForcedRls_AndAppRuntimeGrantsAreMinimal()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            foreach (var table in new[] { "orders", "order_lines" })
            {
                Assert.True(Scalar<bool>(conn,
                    "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = $1", table));
                Assert.Equal(1L, Scalar<long>(conn,
                    "SELECT count(*) FROM pg_policies WHERE tablename = $1 AND policyname = $2", table, table + "_tenant_isolation"));
                Assert.True(Scalar<bool>(conn, "SELECT has_table_privilege('app_runtime', $1, 'SELECT')", table));
                Assert.True(Scalar<bool>(conn, "SELECT has_table_privilege('app_runtime', $1, 'INSERT')", table));
                Assert.False(Scalar<bool>(conn, "SELECT has_table_privilege('app_runtime', $1, 'DELETE')", table));
                Assert.False(Scalar<bool>(conn, "SELECT has_table_privilege('public', $1, 'SELECT')", table));
            }
            // Only the delivery state of an order evolves; its identity and number never change.
            Assert.False(Scalar<bool>(conn, "SELECT has_table_privilege('app_runtime', 'order_lines', 'UPDATE')"));
            Assert.False(Scalar<bool>(conn, "SELECT has_column_privilege('app_runtime', 'orders', 'sequence', 'UPDATE')"));
            Assert.False(Scalar<bool>(conn, "SELECT has_column_privilege('app_runtime', 'orders', 'branch_code', 'UPDATE')"));
            Assert.True(Scalar<bool>(conn, "SELECT has_column_privilege('app_runtime', 'orders', 'status', 'UPDATE')"));
            Assert.True(Scalar<bool>(conn, "SELECT has_column_privilege('app_runtime', 'orders', 'pending_reason', 'UPDATE')"));
        });
    }

    [Fact]
    public void TheMigration_IsIdempotent_AndKeepsExistingRows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var (org, branch) = SeedOrgAndBranch(conn);
            InsertOrder(conn, org, branch, Guid.NewGuid(), 1, customerId: Guid.NewGuid());

            PostgresTestFixture.ApplyMigration(conn, MigrationFile);

            Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM orders"));
        });
    }

    [Fact]
    public void TheOrderOriginInvariant_IsEnforcedByTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var (org, branch) = SeedOrgAndBranch(conn);

            // Registered without a customer, registered with guest data, guest with a customer, guest without data.
            AssertViolation(PostgresErrorCodes.CheckViolation, () => InsertOrder(conn, org, branch, Guid.NewGuid(), 1));
            AssertViolation(PostgresErrorCodes.CheckViolation, () => InsertOrder(conn, org, branch, Guid.NewGuid(), 2,
                customerId: Guid.NewGuid(), guestDocument: "30111222"));
            AssertViolation(PostgresErrorCodes.CheckViolation, () => InsertOrder(conn, org, branch, Guid.NewGuid(), 3,
                customerId: Guid.NewGuid(), origin: "Guest"));
            AssertViolation(PostgresErrorCodes.CheckViolation, () => InsertOrder(conn, org, branch, Guid.NewGuid(), 4, origin: "Guest"));
            AssertViolation(PostgresErrorCodes.CheckViolation, () => InsertOrder(conn, org, branch, Guid.NewGuid(), 5,
                customerId: Guid.NewGuid(), origin: "Unknown"));
        });
    }

    [Fact]
    public void ASequence_IsUniquePerBranch_AndTheBranchMustBelongToTheOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var (org, branch) = SeedOrgAndBranch(conn);
            var (otherOrg, otherBranch) = SeedOrgAndBranch(conn);

            InsertOrder(conn, org, branch, Guid.NewGuid(), 1, customerId: Guid.NewGuid());
            AssertViolation(PostgresErrorCodes.UniqueViolation, () =>
                InsertOrder(conn, org, branch, Guid.NewGuid(), 1, customerId: Guid.NewGuid()));
            AssertViolation(PostgresErrorCodes.CheckViolation, () =>
                InsertOrder(conn, org, branch, Guid.NewGuid(), 0, customerId: Guid.NewGuid()));

            // The same sequence in another branch is fine; a branch of ANOTHER organization is not.
            InsertOrder(conn, otherOrg, otherBranch, Guid.NewGuid(), 1, customerId: Guid.NewGuid());
            AssertViolation(PostgresErrorCodes.ForeignKeyViolation, () =>
                InsertOrder(conn, org, otherBranch, Guid.NewGuid(), 9, customerId: Guid.NewGuid()));
        });
    }

    [Fact]
    public void RowLevelSecurity_HidesAnotherOrganizationsOrders_AndLinesFollowTheirOrder()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        WithScratchDatabase(conn =>
        {
            var (org, branch) = SeedOrgAndBranch(conn);
            var (otherOrg, _) = SeedOrgAndBranch(conn);
            var orderId = Guid.NewGuid();
            InsertOrder(conn, org, branch, orderId, 1, customerId: Guid.NewGuid());
            Exec(conn,
                """
                INSERT INTO order_lines (organization_id, order_id, line_no, product_id, product_name, presentation_id,
                    presentation_name, quantity_behavior, unit_id, quantity, unit_list_price, applied_discount_percentage,
                    unit_net_price, line_total)
                VALUES ($1, $2, 1, $3, 'P', $4, 'Pres', 'FixedQuantity', $5, 2, 10.50, 0, 10.50, 21.00)
                """, org, orderId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            Exec(conn, "SET ROLE app_runtime");
            try
            {
                Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", otherOrg.ToString());
                Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM orders"));
                Assert.Equal(0L, Scalar<long>(conn, "SELECT count(*) FROM order_lines"));

                Exec(conn, "SELECT set_config('app.current_org_id', $1, false)", org.ToString());
                Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM orders"));
                Assert.Equal(1L, Scalar<long>(conn, "SELECT count(*) FROM order_lines"));
            }
            finally
            {
                Exec(conn, "RESET ROLE");
            }
        });
    }
}
