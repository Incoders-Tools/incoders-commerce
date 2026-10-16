using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `004_vaca_verde_customer_price_lists.sql`: customer-price-lists T1. Mostrador turns from final prices into BASE
/// prices (effective from 2026-10-02, the 2026-10-01 finals stay as history) with its own rate set, Reparto becomes its
/// floor, Reparto becomes the organization default for customers and every untouched customer's list, and the old
/// "Clientes" list is dropped when nothing but its own seeded entries references it.
/// </summary>
public sealed partial class VacaVerdeSuppliersCatalogSeedTests
{
    private static readonly DateOnly BaseFrom = new(2026, 10, 2);
    private const int SharedBetweenMostradorAndReparto = 11; // the 11 cuts of the "Productos unidos" list of report-catalogo.md

    private static Guid AddCustomer(NpgsqlConnection owner, Guid orgId, string name, Guid? priceListId = null)
    {
        var id = Guid.NewGuid();
        Exec(owner,
            "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, price_list_id) VALUES ($1, $2, 'Wholesale', $3, $4, $5)",
            id, orgId, name, Guid.NewGuid(), (object?)priceListId ?? DBNull.Value);
        return id;
    }

    private static decimal EntryOn(NpgsqlConnection owner, Guid orgId, string list, string product, DateOnly on) =>
        Scalar<decimal>(owner,
            """
            SELECT e.unit_price FROM price_list_entries e
            JOIN price_lists l ON l.id = e.price_list_id
            JOIN presentations pr ON pr.id = e.presentation_id JOIN products p ON p.id = pr.product_id
            WHERE e.organization_id = $1 AND l.name = $2 AND p.name = $3 AND e.effective_from <= $4
            ORDER BY e.effective_from DESC LIMIT 1
            """, orgId, list, product, on.ToDateTime(TimeOnly.MinValue));

    [Fact]
    public async Task CustomerListsSeed_MostradorBecomesBase_ResolvesBolaDeLomoAt16872_AndRepartoStaysAt16530()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        try
        {
            ApplyCatalog(owner);
            ApplyCustomerLists(owner);
            var scope = new CloudTenantScope(org.OrgId, BranchId: org.BranchId);
            var priceStore = new PostgresPriceListStore(dataSource);
            var componentStore = new PostgresRateComponentStore(dataSource);

            async Task<PriceResolutionOutcome> Resolve(string list, string product, DateOnly on)
            {
                var listId = ListId(owner, org.OrgId, list);
                var service = new PricingResolutionService(
                    new PostgresEffectivePriceSource(priceStore, scope, listId),
                    new PostgresRateComponentSource(componentStore, scope, listId));
                return await service.ResolveAsync(PresentationId(owner, org.OrgId, product), 1m, null, on, CancellationToken.None);
            }

            Assert.Equal(16530m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Reparto", "Bola de lomo", ResolveOn)).UnitListPrice);
            Assert.Equal(16872m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Mostrador", "Bola de lomo", ResolveOn)).UnitListPrice); // 11.400 x 1,48

            // Counter-only cut: base = round(final / 1,48, 2), so the composed price is the old final to the cent.
            Assert.Equal(7817.57m, EntryOn(owner, org.OrgId, "Mostrador", "Lengua", ResolveOn));
            Assert.Equal(11570m, Commerce.Domain.Pricing.Money.Round2(
                Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Mostrador", "Lengua", ResolveOn)).UnitListPrice));
            Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(await Resolve("Mostrador", "Asado completo", ResolveOn));

            // History is kept: the day before, Mostrador still resolves its old final price.
            Assert.Equal(18000m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Mostrador", "Bola de lomo", new DateOnly(2026, 10, 1))).UnitListPrice);
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_PublishesTheMostradorBaseAndItsOwnRateSet_WithoutTouchingRepartoOrTheOldEntries()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            ApplyCustomerLists(owner);

            long Entries(string list, string date) => Scalar<long>(owner,
                "SELECT count(*) FROM price_list_entries e JOIN price_lists l ON l.id = e.price_list_id WHERE e.organization_id = $1 AND l.name = $2 AND e.effective_from = DATE '" + date + "'", org.OrgId, list);
            Assert.Equal(ExpectedMostradorEntries, Entries("Mostrador", "2026-10-01")); // history kept
            Assert.Equal(ExpectedMostradorEntries, Entries("Mostrador", "2026-10-02")); // new base prices
            Assert.Equal(ExpectedRepartoEntries, Entries("Reparto", "2026-10-01"));
            Assert.Equal(0L, Entries("Reparto", "2026-10-02"));

            // Where Reparto has the product, Mostrador's base IS Reparto's base.
            Assert.Equal(11400m, EntryOn(owner, org.OrgId, "Mostrador", "Bola de lomo", BaseFrom));
            Assert.Equal(22500m, EntryOn(owner, org.OrgId, "Mostrador", "Entraña", BaseFrom));

            // Mostrador's own LIST-SPECIFIC set: IVA + IB + Remarcacion 35 on the base, no flete.
            var mostrador = ListId(owner, org.OrgId, "Mostrador");
            Assert.Equal(2L, Count(owner, "rate_component_sets", org.OrgId));
            Assert.Equal(3L, Scalar<long>(owner,
                "SELECT count(*) FROM rate_components c JOIN rate_component_sets s ON s.id = c.set_id WHERE s.price_list_id = $1 AND s.effective_from = DATE '2026-10-02' AND c.calculation_base = 'Base'", mostrador));
            Assert.Equal(48m, Scalar<decimal>(owner,
                "SELECT sum(c.percentage) FROM rate_components c JOIN rate_component_sets s ON s.id = c.set_id WHERE s.price_list_id = $1", mostrador));
            Assert.Equal(35m, Scalar<decimal>(owner,
                "SELECT c.percentage FROM rate_components c JOIN rate_component_sets s ON s.id = c.set_id WHERE s.price_list_id = $1 AND c.code = 'REMARCACION'", mostrador));
            Assert.Equal(0L, Scalar<long>(owner,
                "SELECT count(*) FROM rate_components c JOIN rate_component_sets s ON s.id = c.set_id WHERE s.price_list_id = $1 AND c.code = 'FLETE'", mostrador));

            // Mostrador stays the default (walk-in) list.
            Assert.Equal("Mostrador", Scalar<string>(owner, "SELECT name FROM price_lists WHERE organization_id = $1 AND is_default", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_MostradorNeverPricesBelowReparto_ForEveryProductInBothLists()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            ApplyCustomerLists(owner);

            Assert.Equal(ListId(owner, org.OrgId, "Reparto"),
                Scalar<Guid>(owner, "SELECT floor_price_list_id FROM price_lists WHERE organization_id = $1 AND name = 'Mostrador'", org.OrgId));
            Assert.True(Scalar<bool>(owner, "SELECT floor_price_list_id IS NULL FROM price_lists WHERE organization_id = $1 AND name = 'Reparto'", org.OrgId));

            // Mostrador x 1,48 against Reparto x 1,45, both rounded to the cent, for every product in both lists.
            const string shared =
                """
                FROM price_list_entries m
                JOIN price_lists ml ON ml.id = m.price_list_id AND ml.name = 'Mostrador' AND m.effective_from = DATE '2026-10-02'
                JOIN price_list_entries r ON r.presentation_id = m.presentation_id AND r.effective_from = DATE '2026-10-01'
                JOIN price_lists rl ON rl.id = r.price_list_id AND rl.name = 'Reparto'
                WHERE m.organization_id = $1
                """;
            Assert.Equal(SharedBetweenMostradorAndReparto, Scalar<long>(owner, "SELECT count(*) " + shared, org.OrgId));
            Assert.Equal(0L, Scalar<long>(owner,
                "SELECT count(*) " + shared + " AND round(m.unit_price * 1.48, 2) < round(r.unit_price * 1.45, 2)", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_MakesRepartoTheOrganizationDefaultAndEveryCustomersList_OnlyWhileUntouched()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            var untouched = AddCustomer(owner, org.OrgId, "Cliente sin lista");
            var mostrador = ListId(owner, org.OrgId, "Mostrador");
            var chosen = AddCustomer(owner, org.OrgId, "Cliente en Mostrador", mostrador);

            ApplyCustomerLists(owner);

            var reparto = ListId(owner, org.OrgId, "Reparto");
            Assert.Equal(reparto, Scalar<Guid>(owner, "SELECT default_customer_price_list_id FROM organizations WHERE id = $1", org.OrgId));
            Assert.Equal(reparto, Scalar<Guid>(owner, "SELECT price_list_id FROM customers WHERE id = $1", untouched));
            Assert.Equal(mostrador, Scalar<Guid>(owner, "SELECT price_list_id FROM customers WHERE id = $1", chosen)); // a choice is never overwritten
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_IsIdempotent_AndNeverOverwritesLaterEdits()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            AddCustomer(owner, org.OrgId, "Cliente");
            ApplyCustomerLists(owner);

            (long, long, long, long) Snapshot() => (Count(owner, "price_list_entries", org.OrgId), Count(owner, "rate_component_sets", org.OrgId),
                Count(owner, "rate_components", org.OrgId), Count(owner, "price_lists", org.OrgId));
            var before = Snapshot();

            // Later edits in the app: the owner moves the organization default to Mostrador and removes the floor.
            Exec(owner, "UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", ListId(owner, org.OrgId, "Mostrador"), org.OrgId);
            Exec(owner, "UPDATE price_lists SET floor_price_list_id = NULL WHERE organization_id = $1 AND name = 'Mostrador'", org.OrgId);
            Exec(owner,
                "INSERT INTO audit_log (actor_kind, actor_id, organization_id, entity_type, entity_id, action) VALUES ('org-user', $1, $2, 'organization', $2, 'organization.settings_updated')",
                Guid.NewGuid(), org.OrgId);

            ApplyCustomerLists(owner);

            Assert.Equal(before, Snapshot());
            Assert.Equal(ListId(owner, org.OrgId, "Mostrador"), Scalar<Guid>(owner, "SELECT default_customer_price_list_id FROM organizations WHERE id = $1", org.OrgId));
            Assert.True(Scalar<bool>(owner, "SELECT floor_price_list_id IS NULL FROM price_lists WHERE organization_id = $1 AND name = 'Mostrador'", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_DropsTheOldClientesList_OnlyWhenNothingButItsSeededEntriesReferencesIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        static Guid AddClientes(NpgsqlConnection owner, Provisioned org, string effectiveFrom)
        {
            var id = Guid.NewGuid();
            Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Clientes', false, $4)", id, org.OrgId, org.BranchId!.Value, Guid.NewGuid());
            Exec(owner,
                "INSERT INTO price_list_entries (id, organization_id, branch_id, price_list_id, presentation_id, unit_price, effective_from, source, created_by_user_id) " +
                "SELECT gen_random_uuid(), organization_id, branch_id, $1, id, 14321, DATE '" + effectiveFrom + "', 'Manual', $2 FROM presentations WHERE organization_id = $3 LIMIT 3",
                id, Guid.NewGuid(), org.OrgId);
            return id;
        }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);

        // Untouched: its seeded entries go with it.
        var plain = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            AddClientes(owner, plain, "2026-10-01");
            ApplyCustomerLists(owner);
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM price_lists WHERE organization_id = $1 AND name = 'Clientes'", plain.OrgId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM price_list_entries e WHERE e.organization_id = $1 AND e.unit_price = 14321", plain.OrgId));
            Assert.Equal(2L, Count(owner, "price_lists", plain.OrgId));
        }
        finally { RemoveVacaVerde(owner); }

        // A customer already uses it: kept, and that customer keeps it.
        var used = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            var clientes = AddClientes(owner, used, "2026-10-01");
            var customer = AddCustomer(owner, used.OrgId, "Usa Clientes", clientes);
            ApplyCustomerLists(owner);
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM price_lists WHERE organization_id = $1 AND name = 'Clientes'", used.OrgId));
            Assert.Equal(clientes, Scalar<Guid>(owner, "SELECT price_list_id FROM customers WHERE id = $1", customer));
        }
        finally { RemoveVacaVerde(owner); }

        // Someone published a price of their own in it: kept.
        var edited = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);
            AddClientes(owner, edited, "2026-11-01");
            ApplyCustomerLists(owner);
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM price_lists WHERE organization_id = $1 AND name = 'Clientes'", edited.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CustomerListsSeed_IsANoOp_WithoutTheCatalogLists()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        RemoveVacaVerde(owner);
        ApplyCustomerLists(owner); // no organization: must not throw

        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCustomerLists(owner); // organization but no Reparto list yet: must not throw nor create anything
            Assert.Equal(1L, Count(owner, "price_lists", org.OrgId));
            Assert.Equal(0L, Count(owner, "rate_component_sets", org.OrgId));
            Assert.True(Scalar<bool>(owner, "SELECT default_customer_price_list_id IS NULL FROM organizations WHERE id = $1", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }
}
