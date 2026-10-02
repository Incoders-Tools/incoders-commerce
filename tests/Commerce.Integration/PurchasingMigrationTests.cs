using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// `0032_purchase_receptions.sql` (receptions and their lines) and `0033_stock.sql` (the append-only stock ledger, minimum
/// levels and the cost history): structure, branch RLS, append-only grants and the invariants the application relies on.
/// </summary>
[Collection("Postgres")]
public sealed class PurchasingMigrationTests
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static NpgsqlConnection OpenApp()
    {
        var app = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        app.Open();
        return app;
    }

    private static void Scope(NpgsqlConnection app, Guid org, Guid? branch)
    {
        Exec(app, "SELECT set_config('app.current_org_id', $1, true)", org.ToString());
        if (branch is { } b) Exec(app, "SELECT set_config('app.current_branch_id', $1, true)", b.ToString());
    }

    private static PostgresException Refused(Action action) => Assert.Throws<PostgresException>(action);

    private static Guid SeedOrg(NpgsqlConnection conn)
    {
        var org = Guid.NewGuid();
        Exec(conn, "INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "Org " + org);
        return org;
    }

    private static Guid SeedSupplier(NpgsqlConnection conn, Guid org)
    {
        var id = Guid.NewGuid();
        Exec(conn, "INSERT INTO suppliers (id, organization_id, display_name, created_by_user_id) VALUES ($1, $2, 'Proveedor', $3)",
            id, org, Guid.NewGuid());
        return id;
    }

    private static Guid InsertMovement(
        NpgsqlConnection conn, Guid org, Guid branch, Guid presentation, decimal quantity, string kind = "Opening",
        string? sourceType = null, Guid? sourceId = null, Guid? sourceLineId = null, Guid? reverses = null)
    {
        var id = Guid.NewGuid();
        Exec(conn,
            """
            INSERT INTO stock_movements
                (id, organization_id, branch_id, presentation_id, quantity, kind, source_type, source_id, source_line_id,
                 reverses_movement_id, occurred_at_utc, created_by_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now(), $11)
            """, id, org, branch, presentation, quantity, kind, sourceType, sourceId, sourceLineId, reverses, Guid.NewGuid());
        return id;
    }

    private static Guid InsertReception(
        NpgsqlConnection conn, Guid org, Guid branch, Guid supplier, string status = "Draft", string? reference = null,
        string documentType = "Invoice", int? sequence = null)
    {
        var id = Guid.NewGuid();
        Exec(conn,
            """
            INSERT INTO purchase_receptions
                (id, organization_id, branch_id, supplier_id, status, document_type, document_reference, occurred_on,
                 total_amount, branch_code, sequence, number, created_by_user_id, confirmed_at_utc, confirmed_by_user_id,
                 voided_at_utc, void_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, DATE '2026-10-01', 0, $8, $9, $10, $11, $12, $13, $14, $15)
            """, id, org, branch, supplier, status, documentType, reference,
            sequence is null ? null : 1, sequence, sequence is null ? null : $"R01-W-{sequence}", Guid.NewGuid(),
            status == "Draft" ? null : DateTimeOffset.UtcNow, status == "Draft" ? null : Guid.NewGuid(),
            status == "Voided" ? DateTimeOffset.UtcNow : null, status == "Voided" ? "error de carga" : null);
        return id;
    }

    [Fact]
    public void Migrations_CreateTheTables_WithForcedRls_AndAreReRunnable()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        ApplyAllMigrations(owner);

        foreach (var table in new[] { "purchase_receptions", "purchase_reception_lines", "stock_movements", "stock_minimums", "presentation_costs" })
        {
            Assert.True(Scalar<bool>(owner,
                "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE relname = $1", table), table);
        }
    }

    [Fact]
    public void StockMovements_AreAppendOnly_UpdateAndDeleteAreRefusedToTheAppRole()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var presentation = Presentation(org, branch);

        using var app = OpenApp();
        foreach (var statement in new[] { "UPDATE stock_movements SET quantity = 99 WHERE id = $1", "DELETE FROM stock_movements WHERE id = $1" })
        {
            using var t = app.BeginTransaction();
            Scope(app, org, branch);
            var inserted = InsertMovement(app, org, branch, presentation, 10m);
            var ex = Refused(() => Exec(app, statement, inserted));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            t.Rollback();
        }

    }

    [Fact]
    public void StockMovements_AreIsolatedByBranch_AndAMovementCannotBeWrittenForAnotherBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branchA = Branch(owner, org, "A");
        var branchB = Branch(owner, org, "B");
        var presentationA = Presentation(org, branchA);
        var presentationB = Presentation(org, branchB);
        InsertMovement(owner, org, branchA, presentationA, 5m);
        InsertMovement(owner, org, branchB, presentationB, 7m);

        using var app = OpenApp();
        using (var tx = app.BeginTransaction())
        {
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM stock_movements")); // no scope: fail closed
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Scope(app, org, branchA);
            Assert.Equal(5m, Scalar<decimal>(app, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements"));
            Assert.Equal(0L, Scalar<long>(app, "SELECT count(*) FROM stock_movements WHERE branch_id = $1", branchB));
            Refused(() => InsertMovement(app, org, branchB, presentationB, 1m)); // WITH CHECK: not the selected branch
            tx.Rollback();
        }

        using (var tx = app.BeginTransaction())
        {
            Scope(app, org, branchB);
            Assert.Equal(7m, Scalar<decimal>(app, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements"));
            tx.Rollback();
        }

        // A movement cannot point at a presentation of another branch (composite foreign key).
        using (var tx = app.BeginTransaction())
        {
            Scope(app, org, branchA);
            var fk = Refused(() => InsertMovement(app, org, branchA, presentationB, 1m));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, fk.SqlState);
            tx.Rollback();
        }
    }

    [Theory]
    [InlineData("Sale", 3, "stock_movements_sign_ck")]
    [InlineData("PurchaseReceipt", -3, "stock_movements_sign_ck")]
    [InlineData("Shrinkage", 1, "stock_movements_sign_ck")]
    [InlineData("Opening", -1, "stock_movements_sign_ck")]
    [InlineData("Adjustment", 0, "stock_movements_quantity_nonzero_ck")]
    public void StockMovements_RefuseAQuantityWithTheWrongSignForItsKind(string kind, decimal quantity, string constraint)
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var presentation = Presentation(org, branch);

        var ex = Refused(() => InsertMovement(owner, org, branch, presentation, quantity, kind));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(constraint, ex.ConstraintName);
    }

    [Fact]
    public void StockMovements_SourceLineIsAnIdempotencyKey_PerBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branchA = Branch(owner, org, "A");
        var branchB = Branch(owner, org, "B");
        var presentationA = Presentation(org, branchA);
        var presentationB = Presentation(org, branchB);
        var sale = Guid.NewGuid();
        var line = Guid.NewGuid();

        InsertMovement(owner, org, branchA, presentationA, -2.5m, "Sale", "PosSale", sale, line);

        var duplicate = Refused(() => InsertMovement(owner, org, branchA, presentationA, -2.5m, "Sale", "PosSale", sale, line));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("stock_movements_source_line_uk", duplicate.ConstraintName);

        // Another source type with the same line id (a void), another branch and a null line id are all different keys.
        InsertMovement(owner, org, branchA, presentationA, 2.5m, "Adjustment", "PosSaleVoid", sale, line);
        InsertMovement(owner, org, branchB, presentationB, -1m, "Sale", "PosSale", sale, line);
        InsertMovement(owner, org, branchA, presentationA, 1m, "Opening");
        InsertMovement(owner, org, branchA, presentationA, 1m, "Opening");
    }

    [Fact]
    public void StockMovements_AReversalNamesItsTarget_OnlyOnceAndNeverAReversal()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var presentation = Presentation(org, branch);
        var original = InsertMovement(owner, org, branch, presentation, 120m, "PurchaseReceipt");

        var noTarget = Refused(() => InsertMovement(owner, org, branch, presentation, -120m, "Reversal"));
        Assert.Equal("stock_movements_reversal_link", noTarget.ConstraintName);
        var notReversal = Refused(() => InsertMovement(owner, org, branch, presentation, -120m, "Adjustment", reverses: original));
        Assert.Equal("stock_movements_reversal_link", notReversal.ConstraintName);

        var reversal = InsertMovement(owner, org, branch, presentation, -120m, "Reversal", "PurchaseReceptionVoid", Guid.NewGuid(), Guid.NewGuid(), original);

        var twice = Refused(() => InsertMovement(owner, org, branch, presentation, -120m, "Reversal", reverses: original));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, twice.SqlState);
        Assert.Equal("stock_movements_one_reversal_uk", twice.ConstraintName);

        var ofReversal = Refused(() => InsertMovement(owner, org, branch, presentation, 120m, "Reversal", reverses: reversal));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ofReversal.SqlState);
    }

    [Fact]
    public void Receptions_ConfirmedDocumentIsUniquePerSupplierTypeAndReference_IgnoringCaseAndSpaces()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branchA = Branch(owner, org, "A");
        var branchB = Branch(owner, org, "B");
        var supplier = SeedSupplier(owner, org);

        InsertReception(owner, org, branchA, supplier, "Confirmed", "A-0001-123", sequence: 1);

        // Drafts never count; another type or another supplier is another document.
        InsertReception(owner, org, branchA, supplier, "Draft", "A-0001-123");
        InsertReception(owner, org, branchA, supplier, "Confirmed", "A-0001-123", "DeliveryNote", sequence: 2);
        InsertReception(owner, org, branchA, SeedSupplier(owner, org), "Confirmed", "A-0001-123", sequence: 3);
        InsertReception(owner, org, branchA, supplier, "Confirmed", reference: null, sequence: 4);
        InsertReception(owner, org, branchA, supplier, "Confirmed", reference: null, sequence: 5);

        // The same supplier document is refused even from another branch of the organization.
        var duplicate = Refused(() => InsertReception(owner, org, branchB, supplier, "Confirmed", "  a-0001-123 ", sequence: 1));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("purchase_receptions_confirmed_document_uk", duplicate.ConstraintName);

        // A voided reception frees its document.
        InsertReception(owner, org, branchB, supplier, "Voided", "A-0001-123", sequence: 2);
    }

    [Fact]
    public void Receptions_ANumberIsUniquePerBranch_AndARestrictedStatusNeedsItsNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var supplier = SeedSupplier(owner, org);

        InsertReception(owner, org, branch, supplier, "Confirmed", "X-1", sequence: 1);
        var duplicate = Refused(() => InsertReception(owner, org, branch, supplier, "Confirmed", "X-2", sequence: 1));
        Assert.Equal("purchase_receptions_number_uk", duplicate.ConstraintName);

        var numberless = Refused(() => InsertReception(owner, org, branch, supplier, "Confirmed", "X-3", sequence: null));
        Assert.Equal("purchase_receptions_numbered_ck", numberless.ConstraintName);
    }

    [Fact]
    public void ReceptionLines_CanOnlyChangeWhileTheReceptionIsDraft_AndReferenceAPresentationOfTheSameBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branchA = Branch(owner, org, "A");
        var branchB = Branch(owner, org, "B");
        var supplier = SeedSupplier(owner, org);
        var presentationA = Presentation(org, branchA);
        var presentationB = Presentation(org, branchB);
        var draft = InsertReception(owner, org, branchA, supplier);
        var confirmed = InsertReception(owner, org, branchA, supplier, "Confirmed", "C-1", sequence: 1);

        void InsertLine(Guid reception, Guid presentation, decimal quantity = 2m, decimal cost = 10m) => Exec(owner,
            """
            INSERT INTO purchase_reception_lines
                (id, organization_id, branch_id, reception_id, presentation_id, quantity, unit_cost, line_total, sort_order)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 20, 0)
            """, Guid.NewGuid(), org, branchA, reception, presentation, quantity, cost);

        InsertLine(draft, presentationA);

        var frozen = Refused(() => InsertLine(confirmed, presentationA));
        Assert.Contains("draft", frozen.MessageText, StringComparison.OrdinalIgnoreCase);

        var foreign = Refused(() => InsertLine(draft, presentationB));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);

        Assert.Equal(PostgresErrorCodes.CheckViolation, Refused(() => InsertLine(draft, presentationA, quantity: 0m)).SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, Refused(() => InsertLine(draft, presentationA, cost: -1m)).SqlState);
    }

    [Fact]
    public void Receptions_AreIsolatedByBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branchA = Branch(owner, org, "A");
        var branchB = Branch(owner, org, "B");
        var supplier = SeedSupplier(owner, org);
        InsertReception(owner, org, branchA, supplier);
        InsertReception(owner, org, branchB, supplier);

        using var app = OpenApp();
        using var tx = app.BeginTransaction();
        Scope(app, org, branchA);
        Assert.Equal(1L, Scalar<long>(app, "SELECT count(*) FROM purchase_receptions"));
        tx.Rollback();
    }

    [Fact]
    public void StockMinimums_AreUpdatable_UniquePerPresentation_AndNeverNegative()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var presentation = Presentation(org, branch);

        using var app = OpenApp();
        using var tx = app.BeginTransaction();
        Scope(app, org, branch);
        Exec(app, "INSERT INTO stock_minimums (organization_id, branch_id, presentation_id, minimum_quantity) VALUES ($1, $2, $3, 10)", org, branch, presentation);
        Exec(app, "UPDATE stock_minimums SET minimum_quantity = 20 WHERE presentation_id = $1", presentation);
        Assert.Equal(20m, Scalar<decimal>(app, "SELECT minimum_quantity FROM stock_minimums WHERE presentation_id = $1", presentation));
        tx.Rollback();

        var negative = Refused(() => Exec(owner,
            "INSERT INTO stock_minimums (organization_id, branch_id, presentation_id, minimum_quantity) VALUES ($1, $2, $3, -1)", org, branch, presentation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);
    }

    [Fact]
    public void PresentationCosts_AreAppendOnly_ForTheAppRole()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = SeedOrg(owner);
        var branch = Branch(owner, org);
        var supplier = SeedSupplier(owner, org);
        var presentation = Presentation(org, branch);
        var reception = InsertReception(owner, org, branch, supplier, "Confirmed", "K-1", sequence: 1);

        using var app = OpenApp();
        using var tx = app.BeginTransaction();
        Scope(app, org, branch);
        var id = Guid.NewGuid();
        Exec(app,
            """
            INSERT INTO presentation_costs (id, organization_id, branch_id, presentation_id, supplier_id, reception_id, unit_cost, occurred_on)
            VALUES ($1, $2, $3, $4, $5, $6, 4000, DATE '2026-10-01')
            """, id, org, branch, presentation, supplier, reception);
        var ex = Refused(() => Exec(app, "UPDATE presentation_costs SET unit_cost = 1 WHERE id = $1", id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public void DevInitSnapshot_CarriesThePurchasingMigrationsVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        foreach (var file in new[] { "0032_purchase_receptions.sql", "0033_stock.sql" })
        {
            Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", file))), init);
        }
    }
}
