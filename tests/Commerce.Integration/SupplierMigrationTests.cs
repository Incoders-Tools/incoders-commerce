using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0030_suppliers.sql` (supplier categories, suppliers, supplier contacts) and
/// `0031_current_account_movements.sql` (the generic, append-only current-account ledger).
/// </summary>
[Collection("Postgres")]
public sealed class SupplierMigrationTests
{
    private static readonly string[] MigrationFiles = ["0030_suppliers.sql", "0031_current_account_movements.sql"];

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string MigrationsDir => Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        foreach (var file in Directory.GetFiles(MigrationsDir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static void Exec(NpgsqlConnection conn, string sql, params object?[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(NpgsqlConnection conn, string sql, params object[] args)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? default! : (T)value;
    }

    private static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
    }

    private static NpgsqlConnection OpenApp()
    {
        var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();
        return app;
    }

    private static Guid SeedOrg(NpgsqlConnection conn)
    {
        var org = Guid.NewGuid();
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "Org " + org);
        return org;
    }

    private static Guid SeedSupplier(NpgsqlConnection conn, Guid org, string name = "Proveedor")
    {
        var id = Guid.NewGuid();
        Exec(conn, "INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id) VALUES ($1, $2, $3, $4)",
            id, org, name, Guid.NewGuid());
        return id;
    }

    private static Guid InsertMovement(
        NpgsqlConnection conn, Guid org, Guid supplier, string kind = "Invoice", string direction = "Credit",
        decimal amount = 100m, Guid? reverses = null, string? dueOn = null)
    {
        var id = Guid.NewGuid();
        Exec(conn,
            """
            INSERT INTO current_account_movements
                (id, organization_id, party_kind, party_id, supplier_id, kind, direction, amount, occurred_on, due_on,
                 concept, reverses_movement_id, created_by_user_id)
            VALUES ($1, $2, 'Supplier', $3, $3, $4, $5, $6, DATE '2026-10-01', $7::date, 'c', $8, $9)
            """, id, org, supplier, kind, direction, amount, dueOn is null ? null : (object)dueOn, reverses, Guid.NewGuid());
        return id;
    }

    private static PostgresException Refused(Action action) => Assert.Throws<PostgresException>(action);

    [Fact]
    public void Migrations_CreateTheTables_WithForcedRls_AndAreReRunnable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        ApplyAllMigrations(owner);

        foreach (var table in new[] { "supplier_categories", "suppliers", "supplier_contacts", "current_account_movements" })
        {
            Assert.True(Scalar<bool>(owner,
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = $1", table), table);
        }
    }

    [Fact]
    public void Suppliers_AreIsolatedByOrganization_AndCannotReferenceAnotherOrganizationsCategory()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgA = SeedOrg(owner);
        var orgB = SeedOrg(owner);
        SeedSupplier(owner, orgA, "A");
        SeedSupplier(owner, orgB, "B");
        var categoryOfB = Guid.NewGuid();
        Exec(owner, "INSERT INTO supplier_categories (id, organization_id, name, key) VALUES ($1, $2, 'Carne', 'carne')", categoryOfB, orgB);

        using var app = OpenApp();
        using (var tx = app.BeginTransaction())
        {
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM suppliers")); // no scope: fail closed
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM suppliers"));
            Refused(() => Exec(app, "INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id) VALUES ($1, $2, 'X', $3)",
                Guid.NewGuid(), orgB, Guid.NewGuid()));
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
            var fk = Refused(() => Exec(app,
                "INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id, category_id) VALUES ($1, $2, 'X', $3, $4)",
                Guid.NewGuid(), orgA, Guid.NewGuid(), categoryOfB));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
            Assert.Equal("suppliers_category_org_fk", fk.ConstraintName);
            tx.Rollback();
        }
    }

    [Theory]
    [InlineData("'Cuit', NULL, NULL, NULL, NULL", "suppliers_tax_id_requires_type")]
    [InlineData("'None', '30123456789', NULL, NULL, NULL", "suppliers_tax_id_requires_type")]
    [InlineData("'None', NULL, -1, NULL, NULL", "suppliers_payment_terms_days_check")]
    [InlineData("'None', NULL, NULL, '123', NULL", "suppliers_bank_cbu_check")]
    [InlineData("'None', NULL, NULL, NULL, 'abc'", "suppliers_bank_alias_check")]
    [InlineData("'None', NULL, NULL, NULL, 'con espacio'", "suppliers_bank_alias_check")]
    public void Suppliers_RefuseInvalidTaxIdPaymentTermsAndBankData(string values, string constraint)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);

        var ex = Refused(() => Exec(owner,
            $"INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id, tax_id_type, tax_id, payment_terms_days, bank_cbu, bank_alias) VALUES ($1, $2, 'X', $3, {values})",
            Guid.NewGuid(), org, Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);

        // The valid shapes are accepted.
        Exec(owner,
            "INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id, tax_id_type, tax_id, payment_terms_days, bank_cbu, bank_alias) VALUES ($1, $2, 'Ok', $3, 'Cuit', '30123456789', 30, '0123456789012345678901', 'mi.alias-1')",
            Guid.NewGuid(), org, Guid.NewGuid());
    }

    [Fact]
    public void SupplierContacts_AreOrganizationScoped_AtMostOnePrimary_AndCascadeWithTheSupplier()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var otherOrg = SeedOrg(owner);
        var supplier = SeedSupplier(owner, org);
        Exec(owner, "INSERT INTO supplier_contacts (id, organization_id, supplier_id, first_name, is_primary) VALUES ($1, $2, $3, 'Ana', true)",
            Guid.NewGuid(), org, supplier);

        var second = Refused(() => Exec(owner,
            "INSERT INTO supplier_contacts (id, organization_id, supplier_id, first_name, is_primary) VALUES ($1, $2, $3, 'Beto', true)",
            Guid.NewGuid(), org, supplier));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);

        var foreign = Refused(() => Exec(owner,
            "INSERT INTO supplier_contacts (id, organization_id, supplier_id, first_name) VALUES ($1, $2, $3, 'Robado')",
            Guid.NewGuid(), otherOrg, supplier));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);

        Exec(owner, "DELETE FROM suppliers WHERE id = $1", supplier);
        Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM supplier_contacts WHERE supplier_id = $1", supplier));
    }

    [Fact]
    public void Movements_AreAppendOnly_ForAppRuntime_UpdateAndDeleteAreRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var supplier = SeedSupplier(owner, org);
        var movement = InsertMovement(owner, org, supplier);

        using var app = OpenApp();
        using var tx = app.BeginTransaction();
        Exec(app, "SELECT set_config('app.current_org_id', $1, true)", org.ToString());
        Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM current_account_movements"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            Refused(() => Exec(app, "UPDATE current_account_movements SET amount = 1 WHERE id = $1", movement)).SqlState);
        tx.Rollback();

        using var tx2 = app.BeginTransaction();
        Exec(app, "SELECT set_config('app.current_org_id', $1, true)", org.ToString());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            Refused(() => Exec(app, "DELETE FROM current_account_movements WHERE id = $1", movement)).SqlState);
        tx2.Rollback();
    }

    [Fact]
    public void Movements_AreIsolatedByOrganization_ForAppRuntime()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var orgA = SeedOrg(owner);
        var orgB = SeedOrg(owner);
        var supplierA = SeedSupplier(owner, orgA);
        var supplierB = SeedSupplier(owner, orgB);
        InsertMovement(owner, orgA, supplierA);
        InsertMovement(owner, orgB, supplierB);

        using var app = OpenApp();
        using var tx = app.BeginTransaction();
        Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM current_account_movements"));
        Exec(app, "SELECT set_config('app.current_org_id', $1, true)", orgA.ToString());
        Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM current_account_movements"));
        Refused(() => InsertMovement(app, orgB, supplierB));
        tx.Rollback();
    }

    [Fact]
    public void Movements_EnforceTheirShape_InTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var otherOrg = SeedOrg(owner);
        var supplier = SeedSupplier(owner, org);

        Assert.Equal("current_account_movements_amount_check", Refused(() => InsertMovement(owner, org, supplier, amount: 0m)).ConstraintName);
        Assert.Equal("current_account_movements_amount_check", Refused(() => InsertMovement(owner, org, supplier, amount: -5m)).ConstraintName);
        Assert.Equal("current_account_movements_kind_check", Refused(() => InsertMovement(owner, org, supplier, kind: "Bogus")).ConstraintName);
        Assert.Equal("current_account_movements_direction_check", Refused(() => InsertMovement(owner, org, supplier, direction: "Sideways")).ConstraintName);
        Assert.Equal("current_account_movements_due_not_before_occurred", Refused(() => InsertMovement(owner, org, supplier, dueOn: "2026-09-30")).ConstraintName);
        InsertMovement(owner, org, supplier, dueOn: "2026-10-01"); // same day is fine

        // The party must be the supplier the movement is wired to.
        var mismatch = Refused(() => Exec(owner,
            """
            INSERT INTO current_account_movements
                (id, organization_id, party_kind, party_id, supplier_id, kind, direction, amount, occurred_on, concept, created_by_user_id)
            VALUES ($1, $2, 'Supplier', $3, $4, 'Invoice', 'Credit', 1, DATE '2026-10-01', 'c', $5)
            """, Guid.NewGuid(), org, Guid.NewGuid(), supplier, Guid.NewGuid()));
        Assert.Equal("current_account_movements_party_supplier", mismatch.ConstraintName);

        // The supplier must live in the movement's own organization.
        var foreign = Refused(() => InsertMovement(owner, otherOrg, supplier));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
    }

    [Fact]
    public void Reversals_PointAtAMovementOnce_AndAReversalCannotBeReversed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var supplier = SeedSupplier(owner, org);
        var payment = InsertMovement(owner, org, supplier, "Payment", "Debit", 40m);

        // A reversal must name a movement, and a plain movement must not.
        Assert.Equal("current_account_movements_reversal_link",
            Refused(() => InsertMovement(owner, org, supplier, "Reversal", "Credit", 40m)).ConstraintName);
        Assert.Equal("current_account_movements_reversal_link",
            Refused(() => InsertMovement(owner, org, supplier, "Payment", "Debit", 40m, reverses: payment)).ConstraintName);

        var reversal = InsertMovement(owner, org, supplier, "Reversal", "Credit", 40m, reverses: payment);

        var twice = Refused(() => InsertMovement(owner, org, supplier, "Reversal", "Credit", 40m, reverses: payment));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, twice.SqlState);

        var reversalOfReversal = Refused(() => InsertMovement(owner, org, supplier, "Reversal", "Debit", 40m, reverses: reversal));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, reversalOfReversal.SqlState);

        var missing = Refused(() => InsertMovement(owner, org, supplier, "Reversal", "Credit", 40m, reverses: Guid.NewGuid()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missing.SqlState);
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheSupplierMigrationsVerbatim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var initPath = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "dev", "db", "init-rls.sql");
        var init = Lf(File.ReadAllText(initPath));
        foreach (var file in MigrationFiles)
        {
            Assert.Contains(Lf(File.ReadAllText(Path.Combine(MigrationsDir, file))), init);
        }
    }
}
