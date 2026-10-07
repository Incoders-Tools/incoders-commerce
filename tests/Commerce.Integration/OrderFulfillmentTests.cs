using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;
using Commerce.Domain.Tenancy;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// Order fulfillment (0045), against real Postgres as app_runtime: manual status steps, a delivery run from planning to
/// return, remito numbering and data, and what the return moves: per line delivered quantities, the stock (once), and
/// the customer's current account (once, at the delivered value), while an undelivered order goes back for another run.
/// </summary>
[Collection("Postgres")]
public sealed class OrderFulfillmentTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public OrderFulfillmentTests()
    {
        if (!_postgresAvailable) return;
        using (var owner = OpenOwner())
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
            {
                PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
            }
        }
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private sealed record World(Guid Org, Guid Branch, int BranchCode, Guid Actor, Guid Customer, Guid Kilos, Guid Units)
    {
        public CloudTenantScope Scope => new(Org, BranchId: Branch);
    }

    private async Task<World> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Vaca Verde Test"), new NewBranch(branch, "Ruta 51"),
            new NewUserAccount(actor, $"seller-{org}@example.com", "hash", [branch], [new RoleDto("seller", Permission.TakeOrders)]),
            CancellationToken.None));

        var customer = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(new CloudTenantScope(org),
            new NewCustomer(customer, CustomerKind.Wholesale, "Parrilla Don Julio", null, TaxIdType.Cuit, "30712345671",
                TaxCondition.ResponsableInscripto, "2478-555555", null, "Frascheri", "626", null, null, null, null, "Portón verde",
                null, null, null, actor),
            "org-user", actor, CancellationToken.None);

        using var owner = OpenOwner();
        var code = Scalar<short>(owner, "SELECT code FROM branches WHERE id = $1", branch);
        return new World(org, branch, code, actor, customer,
            Presentation(org, branch, "Vacío", "Por kg"), Presentation(org, branch, "Chorizo", "Unidad", "FixedQuantity"));
    }

    private static int _sequence;

    /// <summary>An order as the staff submission stores it: 2,5 kg of Vacío at 10.000 and 3 Chorizos at 500 (customer), or one kilo (guest).</summary>
    private static Guid Order(World w, bool guest = false)
    {
        var orderId = Guid.NewGuid();
        using var owner = OpenOwner();
        var sequence = Interlocked.Increment(ref _sequence) + Random.Shared.Next(1_000, 1_000_000);
        if (guest)
        {
            Exec(owner,
                """
                INSERT INTO orders (organization_id, order_id, destination_branch_id, origin, guest_document_id, guest_channel,
                    guest_contact_address, guest_display_name, status, pending_reason, branch_code, sequence, submitted_at_utc)
                VALUES ($1, $2, $3, 'Guest', '20111222', 'Email', 'guest@example.com', 'Consumidor invitado',
                    'PendingDestination', 'DestinationOffline', $4, $5, now())
                """, w.Org, orderId, w.Branch, (short)w.BranchCode, sequence);
            Line(owner, w, orderId, 1, w.Kilos, "Weighted", 1m, 10_000m);
            return orderId;
        }

        Exec(owner,
            """
            INSERT INTO orders (organization_id, order_id, destination_branch_id, origin, customer_id, status, pending_reason,
                branch_code, sequence, submitted_at_utc)
            VALUES ($1, $2, $3, 'RegisteredCustomer', $4, 'PendingDestination', 'DestinationOffline', $5, $6, now())
            """, w.Org, orderId, w.Branch, w.Customer, (short)w.BranchCode, sequence);
        Line(owner, w, orderId, 1, w.Kilos, "Weighted", 2.5m, 10_000m);
        Line(owner, w, orderId, 2, w.Units, "FixedQuantity", 3m, 500m);
        return orderId;
    }

    private static void Line(NpgsqlConnection owner, World w, Guid orderId, int lineNo, Guid presentation, string behavior, decimal quantity, decimal price) =>
        Exec(owner,
            """
            INSERT INTO order_lines (organization_id, order_id, line_no, product_id, product_name, presentation_id, presentation_name,
                quantity_behavior, unit_id, quantity, unit_list_price, applied_discount_percentage, unit_net_price, line_total)
            VALUES ($1, $2, $3, $4, $5, $6, 'Presentación', $7, $4, $8, $9, 0, $9, $10)
            """, w.Org, orderId, lineNo, Guid.NewGuid(), lineNo == 1 ? "Vacío" : "Chorizo", presentation, behavior, quantity, price, quantity * price);

    private static decimal OnHand(Guid presentation)
    {
        using var owner = OpenOwner();
        return Scalar<decimal>(owner, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements WHERE presentation_id = $1", presentation);
    }

    [Fact]
    public void TheRules_AllowOnlyTheManualSteps_AndJudgeAReturnByWhatWasDelivered()
    {
        Assert.Equal([OrderFulfillmentStatus.InPreparation, OrderFulfillmentStatus.Cancelled],
            OrderFulfillmentRules.ManualTargets(OrderFulfillmentStatus.Confirmed));
        Assert.False(OrderFulfillmentRules.CanMoveManually(OrderFulfillmentStatus.ReadyToDispatch, OrderFulfillmentStatus.OutForDelivery));
        Assert.Empty(OrderFulfillmentRules.ManualTargets(OrderFulfillmentStatus.Delivered));
        Assert.Equal(OrderFulfillmentStatus.ReadyToDispatch, OrderFulfillmentRules.AfterReturn([(2m, 0m), (1m, 0m)]));
        Assert.Equal(OrderFulfillmentStatus.Delivered, OrderFulfillmentRules.AfterReturn([(2.5m, 2.62m), (3m, 3m)]));
        Assert.Equal(OrderFulfillmentStatus.PartiallyDelivered, OrderFulfillmentRules.AfterReturn([(2.5m, 2.4m), (3m, 3m)]));
        Assert.Equal(24_000m, OrderFulfillmentRules.DeliveredAmount(10_000m, 2.4m));
        Assert.False(OrderFulfillmentRules.IsValidDeliveredQuantity(1.2345m));
        Assert.Equal("R01-00000042", new RemitoNumber(new BranchCode(1), 42).Format());
    }

    [Fact]
    public async Task ManualSteps_FollowTheRules_AndCancellingNeedsAReason()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = new PostgresFulfillmentStore(_dataSource!);
        var order = Order(w);

        Assert.Equal(FulfillmentOutcome.InvalidTransition,
            (await store.ChangeStatusAsync(w.Scope, order, OrderFulfillmentStatus.Delivered, null, w.Actor, default)).Outcome);
        Assert.Equal(FulfillmentOutcome.Done,
            (await store.ChangeStatusAsync(w.Scope, order, OrderFulfillmentStatus.InPreparation, null, w.Actor, default)).Outcome);
        Assert.Equal(FulfillmentOutcome.Invalid,
            (await store.ChangeStatusAsync(w.Scope, order, OrderFulfillmentStatus.Cancelled, "  ", w.Actor, default)).Outcome);
        Assert.Equal(FulfillmentOutcome.Done,
            (await store.ChangeStatusAsync(w.Scope, order, OrderFulfillmentStatus.Cancelled, "El cliente lo anuló", w.Actor, default)).Outcome);

        var detail = (await store.GetOrderAsync(w.Scope, order, default))!;
        Assert.Equal(("Cancelled", "El cliente lo anuló"), (detail.Summary.Status, detail.Summary.CancelReason));
        Assert.Empty(detail.AllowedTransitions);
        Assert.Equal(26_500m, detail.Summary.Total); // 2,5 kg x 10.000 + 3 x 500
        Assert.Equal(("Parrilla Don Julio", "30712345671", "Frascheri 626"), (detail.Party.DisplayName, detail.Party.TaxId, detail.Party.Address));
        Assert.Single(await store.ListOrdersAsync(w.Scope, "Cancelled", null, null, "don julio", default));
        Assert.Empty(await store.ListOrdersAsync(w.Scope, "Active", null, null, null, default));
    }

    [Fact]
    public async Task APlannedRun_IsDiscarded_FreeingItsOrders_ButADispatchedOneIsNot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = new PostgresFulfillmentStore(_dataSource!);
        var first = Order(w);
        var second = Order(w);
        var planned = (await store.CreateRunAsync(w.Scope, Today, null, null, null, [first, second], w.Actor, default)).Id!.Value;

        Assert.Equal(FulfillmentOutcome.Done, (await store.DeleteRunAsync(w.Scope, planned, w.Actor, default)).Outcome);
        Assert.Null(await store.GetRunAsync(w.Scope, planned, default));
        Assert.Equal(FulfillmentOutcome.NotFound, (await store.DeleteRunAsync(w.Scope, planned, w.Actor, default)).Outcome);

        // Its orders are free: they join another run.
        var next = await store.CreateRunAsync(w.Scope, Today, null, null, null, [first, second], w.Actor, default);
        Assert.Equal(FulfillmentOutcome.Done, next.Outcome);
        await store.DispatchRunAsync(w.Scope, next.Id!.Value, w.Actor, default);
        Assert.Equal("run-not-planned", (await store.DeleteRunAsync(w.Scope, next.Id!.Value, w.Actor, default)).Error);

        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'delivery-run.deleted' AND entity_id = $1", planned));
    }

    [Fact]
    public async Task ARun_IsPlannedDispatchedAndSettled_MovingStockAndTheCurrentAccountOnce()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = new PostgresFulfillmentStore(_dataSource!);
        var customerOrder = Order(w);
        var guestOrder = Order(w, guest: true);
        var created = await store.CreateRunAsync(w.Scope, Today, "Juan", "Kangoo AB123CD", null, [customerOrder, guestOrder], w.Actor, default);
        Assert.Equal(FulfillmentOutcome.Done, created.Outcome);
        var runId = created.Id!.Value;
        Assert.Equal("order-in-another-run",
            (await store.CreateRunAsync(w.Scope, Today, null, null, null, [customerOrder], w.Actor, default)).Error);

        // Nothing moves before the return: not when planned, not when dispatched.
        Assert.Equal(FulfillmentOutcome.Done, (await store.DispatchRunAsync(w.Scope, runId, w.Actor, default)).Outcome);
        Assert.Equal(0m, OnHand(w.Kilos));

        var run = (await store.GetRunAsync(w.Scope, runId, default))!;
        Assert.Equal(("OutForDelivery", 2, 36_500m), (run.Run.Status, run.Run.OrderCount, run.Run.Total)); // 26.500 + the guest's 10.000
        Assert.All(run.Stops, stop => Assert.Equal("OutForDelivery", stop.Order.Status));
        var code = new BranchCode(w.BranchCode).Format();
        Assert.Equal([$"R{code}-00000001", $"R{code}-00000002"], run.Stops.Select(s => s.Order.RemitoNumber));

        var remitos = await store.GetRemitosAsync(w.Scope, [customerOrder], Today, default);
        var remito = Assert.Single(remitos);
        Assert.Equal(($"R{code}-00000001", "Ruta 51", "Parrilla Don Julio", 26_500m, 1, "Juan"),
            (remito.RemitoNumber, remito.Branch.Name, remito.Customer.DisplayName, remito.Total, remito.RunNumber, remito.DriverName));

        var settled = await store.SettleRunAsync(w.Scope, runId,
        [
            new OrderReturnInput(customerOrder, true, "CurrentAccount", [new LineDeliveryInput(1, 2.4m)]),
            new OrderReturnInput(guestOrder, false, null, null),
        ], w.Actor, Today, default);
        Assert.Equal(FulfillmentOutcome.Done, settled.Outcome);
        Assert.Equal("run-not-out-for-delivery",
            (await store.SettleRunAsync(w.Scope, runId, [new OrderReturnInput(customerOrder, true, null, null)], w.Actor, Today, default)).Error);

        var delivered = (await store.GetOrderAsync(w.Scope, customerOrder, default))!;
        Assert.Equal(("PartiallyDelivered", 25_500m, "CurrentAccount"),
            (delivered.Summary.Status, delivered.Summary.DeliveredTotal, delivered.Summary.Settlement));
        Assert.Equal([2.4m, 3m], delivered.Lines.Select(l => l.DeliveredQuantity!.Value));
        Assert.Equal((-2.4m, -3m), (OnHand(w.Kilos), OnHand(w.Units)));

        using (var money = OpenOwner())
        {
            // On account: due after the organization's default terms (30 days), no money in the treasury yet.
            Assert.Equal(Today.AddDays(30).ToDateTime(TimeOnly.MinValue), Scalar<DateTime>(money,
                "SELECT due_on FROM current_account_movements WHERE customer_id = $1 AND source_type = 'OrderDelivery'", w.Customer));
            Assert.Equal(0L, Scalar<long>(money, "SELECT count(*) FROM treasury_movements WHERE source_id = $1", customerOrder));
        }

        var back = (await store.GetOrderAsync(w.Scope, guestOrder, default))!;
        Assert.Equal(("ReadyToDispatch", (Guid?)null), (back.Summary.Status, back.Summary.RunId));
        Assert.Equal("Completed", (await store.GetRunAsync(w.Scope, runId, default))!.Run.Status);

        using var owner = OpenOwner();
        Assert.Equal(25_500m, Scalar<decimal>(owner,
            "SELECT amount FROM current_account_movements WHERE customer_id = $1 AND direction = 'Debit' AND source_type = 'OrderDelivery'", w.Customer));
        Assert.Equal($"R{code}-00000001", Scalar<string>(owner,
            "SELECT document_reference FROM current_account_movements WHERE customer_id = $1", w.Customer));
    }

    [Fact]
    public async Task TheDocumentData_OfTheOrganizationAndBranch_IsSavedAndPrinted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = new PostgresFulfillmentStore(_dataSource!);

        Assert.True(await store.UpdateOrganizationProfileAsync(w.Scope, new OrganizationDocumentProfile(
            "", "Distribuidora Arrecifes S.R.L.", "30712345671", "ResponsableInscripto", "901-123456-7", new DateOnly(2010, 3, 1),
            "Frascheri 626 - Arrecifes", "Documento no válido como factura.", "https://example.com/logo.png", "#2f7d32"), w.Actor, default));
        Assert.True(await store.UpdateBranchProfileAsync(w.Scope, new BranchDocumentProfile(
            w.Branch, "", 0, "Frascheri 626", "Arrecifes", "2478-123456", null, "Depósito Ruta 51"), w.Actor, default));

        var organization = (await store.GetOrganizationProfileAsync(w.Scope, default))!;
        Assert.Equal(("Vaca Verde Test", "Distribuidora Arrecifes S.R.L.", "#2f7d32"), (organization.Name, organization.LegalName, organization.PrimaryColor));
        var branch = Assert.Single(await store.ListBranchProfilesAsync(w.Scope, default));
        Assert.Equal(("Ruta 51", "Depósito Ruta 51", "2478-123456"), (branch.Name, branch.WarehouseAddress, branch.Phone));

        var remito = Assert.Single(await store.GetRemitosAsync(w.Scope, [Order(w)], Today, default));
        Assert.Equal(("Distribuidora Arrecifes S.R.L.", "Depósito Ruta 51"), (remito.Organization.LegalName, remito.Branch.WarehouseAddress));
        Assert.Equal(Today, remito.IssuedOn);
    }

    [Fact]
    public void TheMigration_IsMirroredVerbatimInTheDevInitScript()
    {
        var root = PostgresTestFixture.RepoRoot();
        var migration = File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0045_order_fulfillment_and_delivery.sql")).Replace("\r\n", "\n");
        var init = File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")).Replace("\r\n", "\n");

        Assert.Contains(migration, init);
    }

    [Fact]
    public async Task ADeliveryPaidOnDelivery_PutsTheMoneyInTheBranchCash_AndShowsSaleAndPaymentOnTheCustomersAccount()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var store = new PostgresFulfillmentStore(_dataSource!);
        var order = Order(w);
        var runId = (await store.CreateRunAsync(w.Scope, Today, null, null, null, [order], w.Actor, default)).Id!.Value;
        await store.DispatchRunAsync(w.Scope, runId, w.Actor, default);

        Assert.Equal(FulfillmentOutcome.Done, (await store.SettleRunAsync(w.Scope, runId,
            [new OrderReturnInput(order, true, "PaidOnDelivery", null)], w.Actor, Today, default)).Outcome);

        using var owner = OpenOwner();
        Assert.Equal(26_500m, Scalar<decimal>(owner,
            """
            SELECT m.amount FROM treasury_movements m JOIN treasury_accounts a ON a.organization_id = m.organization_id AND a.id = m.account_id
            WHERE m.source_id = $1 AND a.kind = 'Cash' AND m.direction = 'In'
            """, order));
        Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM current_account_movements WHERE customer_id = $1", w.Customer));
        Assert.Equal(0m, Scalar<decimal>(owner,
            "SELECT SUM(CASE WHEN direction = 'Debit' THEN amount ELSE -amount END) FROM current_account_movements WHERE customer_id = $1", w.Customer));
    }
}
