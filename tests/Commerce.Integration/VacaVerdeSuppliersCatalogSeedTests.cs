using Commerce.Application.Pricing;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Pricing;
using Commerce.Cloud.Api.Tenancy;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `deploy/db/seeds/vaca-verde/002_vaca_verde_suppliers.sql` and `003_vaca_verde_catalog.sql`: the versioned,
/// idempotent seeds with Vaca Verde's real suppliers and its butcher-shop / distribution catalog (categories,
/// products, "Por kg" presentations, and the Mostrador / Reparto price lists of branch "Ruta 51"). The customer
/// price lists seed (`004`) is covered in the `.CustomerLists` part of this class.
/// Both resolve the organization by name, the creating user from its business admin (and the catalog also the
/// branch by name) and do nothing when any of them is missing.
/// </summary>
[Collection("Postgres")]
public sealed partial class VacaVerdeSuppliersCatalogSeedTests
{
    private const int ExpectedSuppliers = 9;
    private const int ExpectedSupplierContacts = 8;
    private const int ExpectedCategories = 8;
    private const int ExpectedProducts = 90;
    private const int ExpectedMostradorEntries = 74;
    private const int ExpectedRepartoEntries = 25;
    private static readonly DateOnly ResolveOn = new(2026, 10, 2);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    private static string SeedSql(string file) =>
        File.ReadAllText(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "seeds", "vaca-verde", file));

    private static void ApplyAllMigrations(NpgsqlConnection owner)
    {
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static NpgsqlConnection OpenOwner()
    {
        var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        return owner;
    }

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

    private static void ApplySuppliers(NpgsqlConnection owner) => Exec(owner, SeedSql("002_vaca_verde_suppliers.sql"));

    private static void ApplyCatalog(NpgsqlConnection owner) => Exec(owner, SeedSql("003_vaca_verde_catalog.sql"));

    private static void ApplyCustomerLists(NpgsqlConnection owner) => Exec(owner, SeedSql("004_vaca_verde_customer_price_lists.sql"));

    private static void ApplyBoth(NpgsqlConnection owner)
    {
        ApplySuppliers(owner);
        ApplyCatalog(owner);
    }

    private static void RemoveVacaVerde(NpgsqlConnection owner)
    {
        // Categories are ON DELETE RESTRICT for their products: remove the products first.
        Exec(owner, "DELETE FROM products WHERE organization_id IN (SELECT id FROM organizations WHERE lower(name) = 'vaca verde')");
        Exec(owner, "DELETE FROM customers WHERE organization_id IN (SELECT id FROM organizations WHERE lower(name) = 'vaca verde')");
        Exec(owner, "DELETE FROM users WHERE organization_id IN (SELECT id FROM organizations WHERE lower(name) = 'vaca verde')");
        Exec(owner, "DELETE FROM organizations WHERE lower(name) = 'vaca verde'");
    }

    private sealed record Provisioned(Guid OrgId, Guid? BranchId);

    private static Provisioned ProvisionVacaVerde(
        NpgsqlConnection owner, bool withBusinessAdmin = true, bool withBranch = true, bool withDefaultList = true)
    {
        RemoveVacaVerde(owner);
        var orgId = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Vaca Verde')", orgId);
        if (withBusinessAdmin)
        {
            Exec(owner,
                "INSERT INTO users (id, organization_id, email, password_hash, roles) VALUES ($1, $2, $3, 'x', '[{\"name\":\"business-admin\",\"permissions\":0}]'::jsonb)",
                Guid.NewGuid(), orgId, $"admin-{orgId:N}@vacaverde.test");
        }

        Guid? branchId = null;
        if (withBranch)
        {
            branchId = Guid.NewGuid();
            Exec(owner, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Ruta 51')", branchId.Value, orgId);
            if (withDefaultList)
            {
                // What account provisioning leaves behind: an empty default list named "Default".
                Exec(owner,
                    "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Default', true, $4)",
                    Guid.NewGuid(), orgId, branchId.Value, Guid.NewGuid());
            }
        }

        return new Provisioned(orgId, branchId);
    }

    private static long Count(NpgsqlConnection owner, string table, Guid orgId) =>
        Scalar<long>(owner, $"SELECT count(*) FROM {table} WHERE organization_id = $1", orgId);

    private static Guid ListId(NpgsqlConnection owner, Guid orgId, string name) =>
        Scalar<Guid>(owner, "SELECT id FROM price_lists WHERE organization_id = $1 AND name = $2", orgId, name);

    private static Guid PresentationId(NpgsqlConnection owner, Guid orgId, string productName) =>
        Scalar<Guid>(owner,
            "SELECT pr.id FROM presentations pr JOIN products p ON p.id = pr.product_id WHERE p.organization_id = $1 AND p.name = $2",
            orgId, productName);

    // ------------------------------------------------------------------ suppliers

    [Fact]
    public void SuppliersSeed_LoadsTheNineSuppliers_WithTheirContactsCategoryAndCities()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplySuppliers(owner);

            Assert.Equal(ExpectedSuppliers, Count(owner, "suppliers", org.OrgId));
            Assert.Equal(ExpectedSupplierContacts, Count(owner, "supplier_contacts", org.OrgId));
            Assert.Equal("Carne", Scalar<string>(owner, "SELECT name FROM supplier_categories WHERE organization_id = $1", org.OrgId));
            Assert.Equal(ExpectedSuppliers, Scalar<long>(owner,
                "SELECT count(*) FROM suppliers s JOIN supplier_categories c ON c.id = s.category_id WHERE s.organization_id = $1 AND c.key = 'carne'", org.OrgId));

            // The two "Villarino ... FRIMSA" rows are ONE supplier with two contacts, each with its own phone.
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM suppliers WHERE organization_id = $1 AND display_name = 'Frimsa'", org.OrgId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM suppliers WHERE organization_id = $1 AND display_name ILIKE '%villarino%'", org.OrgId));
            Assert.Equal(2L, Scalar<long>(owner,
                "SELECT count(*) FROM supplier_contacts c JOIN suppliers s ON s.id = c.supplier_id WHERE s.organization_id = $1 AND s.display_name = 'Frimsa' AND c.last_name = 'Villarino' AND c.phone ~ '^[0-9]{10}$'", org.OrgId));
            Assert.Equal("Agustín", Scalar<string>(owner,
                "SELECT c.first_name FROM supplier_contacts c JOIN suppliers s ON s.id = c.supplier_id WHERE s.organization_id = $1 AND s.display_name = 'Frimsa' AND c.is_primary", org.OrgId));

            // The department's contact is first-name only with the "Depósito" role.
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM supplier_contacts c JOIN suppliers s ON s.id = c.supplier_id WHERE s.organization_id = $1 AND s.display_name = 'Ventas DF Distribuidora' AND c.first_name = 'Gustavo' AND c.last_name IS NULL AND c.role = 'Depósito'", org.OrgId));
            // Never guess a split of a cell that is not clearly first + last.
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM supplier_contacts c WHERE c.organization_id = $1 AND c.first_name = 'Nelson Luis Beber Valdemarin' AND c.last_name IS NULL", org.OrgId));

            // Phones are digits only; cities are the global Georef rows.
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM suppliers WHERE organization_id = $1 AND phone !~ '^[0-9]+$'", org.OrgId));
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM suppliers s JOIN cities ci ON ci.id = s.city_id WHERE s.organization_id = $1 AND s.display_name = 'Swift' AND ci.indec_id = '82084270' AND ci.name = 'Rosario'", org.OrgId));
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM suppliers s JOIN cities ci ON ci.id = s.city_id WHERE s.organization_id = $1 AND s.display_name = 'Carnicería Cow' AND ci.indec_id = '02014010' AND s.address_street = 'Av. Nazca' AND s.address_number = '5096'", org.OrgId));
            Assert.Equal(1L, Scalar<long>(owner,
                "SELECT count(*) FROM suppliers s JOIN cities ci ON ci.id = s.city_id WHERE s.organization_id = $1 AND s.display_name = 'Amalia' AND ci.name = 'San Pedro' AND ci.province_id = '06'", org.OrgId));
            Assert.Equal(6L, Scalar<long>(owner, "SELECT count(*) FROM suppliers WHERE organization_id = $1 AND city_id IS NOT NULL", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void SuppliersSeed_IsIdempotent_AndPreservesLaterEdits()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplySuppliers(owner);
            Exec(owner, "UPDATE suppliers SET notes = 'editado por el dueno' WHERE organization_id = $1 AND display_name = 'Swift'", org.OrgId);
            ApplySuppliers(owner);

            Assert.Equal(ExpectedSuppliers, Count(owner, "suppliers", org.OrgId));
            Assert.Equal(ExpectedSupplierContacts, Count(owner, "supplier_contacts", org.OrgId));
            Assert.Equal(1L, Count(owner, "supplier_categories", org.OrgId));
            Assert.Equal("editado por el dueno", Scalar<string>(owner, "SELECT notes FROM suppliers WHERE organization_id = $1 AND display_name = 'Swift'", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void SuppliersSeed_IsANoOp_WithoutTheOrganization_OrItsBusinessAdmin()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        RemoveVacaVerde(owner);
        var before = (Scalar<long>(owner, "SELECT count(*) FROM suppliers"), Scalar<long>(owner, "SELECT count(*) FROM supplier_categories"));

        ApplySuppliers(owner); // must not throw
        Assert.Equal(before, (Scalar<long>(owner, "SELECT count(*) FROM suppliers"), Scalar<long>(owner, "SELECT count(*) FROM supplier_categories")));

        var org = ProvisionVacaVerde(owner, withBusinessAdmin: false);
        try
        {
            ApplySuppliers(owner); // must not throw
            Assert.Equal(0L, Count(owner, "suppliers", org.OrgId));
            Assert.Equal(0L, Count(owner, "supplier_categories", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    // -------------------------------------------------------------------- catalog

    [Fact]
    public void CatalogSeed_LoadsCategoriesProductsAndWeightedPresentations_WithSequentialCodes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyCatalog(owner);

            Assert.Equal(ExpectedCategories, Count(owner, "categories", org.OrgId));
            Assert.Equal(ExpectedProducts, Count(owner, "products", org.OrgId));
            Assert.Equal(ExpectedProducts, Count(owner, "presentations", org.OrgId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM products WHERE organization_id = $1 AND branch_id <> $2", org.OrgId, org.BranchId!.Value));

            // One "Por kg" Weighted presentation per product, kg, unique sequential codes 001..090.
            Assert.Equal(ExpectedProducts, Scalar<long>(owner,
                "SELECT count(*) FROM presentations WHERE organization_id = $1 AND name = 'Por kg' AND quantity_behavior = 'Weighted' AND unit_id = md5('vaca-verde:unit:kg')::uuid", org.OrgId));
            Assert.Equal(ExpectedProducts, Scalar<long>(owner, "SELECT count(DISTINCT identification_code) FROM presentations WHERE organization_id = $1", org.OrgId));
            Assert.Equal("001", Scalar<string>(owner, "SELECT min(identification_code) FROM presentations WHERE organization_id = $1", org.OrgId));
            Assert.Equal("090", Scalar<string>(owner, "SELECT max(identification_code) FROM presentations WHERE organization_id = $1", org.OrgId));

            // Names are unique and land in the right category.
            Assert.Equal(ExpectedProducts, Scalar<long>(owner, "SELECT count(DISTINCT lower(name)) FROM products WHERE organization_id = $1", org.OrgId));
            string CategoryOf(string product) => Scalar<string>(owner,
                "SELECT c.name FROM products p JOIN categories c ON c.id = p.category_id WHERE p.organization_id = $1 AND p.name = $2", org.OrgId, product);
            Assert.Equal("Vacuno", CategoryOf("Bola de lomo"));
            Assert.Equal("Vacuno", CategoryOf("Vacío"));
            Assert.Equal("Vacuno", CategoryOf("Vacío novillo seleccionado"));
            Assert.Equal("Vacuno", CategoryOf("Vacío marca Santa Inés"));
            Assert.Equal("Vacuno", CategoryOf("Asado completo"));
            Assert.Equal("Cerdo", CategoryOf("Matambre de cerdo"));
            Assert.Equal("Cerdo", CategoryOf("Costilla de cerdo"));
            Assert.Equal("Cerdo", CategoryOf("Bondiola"));
            Assert.Equal("Embutidos", CategoryOf("Bondiola salada"));
            Assert.Equal("Pollo", CategoryOf("Pollo entero"));
            Assert.Equal("Milanesas", CategoryOf("Medallón de pollo jamón y queso"));
            Assert.Equal("Milanesas", CategoryOf("Milanesa de ternera de bola de lomo"));
            Assert.Equal("Achuras", CategoryOf("Hígado"));
            Assert.Equal(0L, Scalar<long>(owner,
                "SELECT count(*) FROM products p JOIN categories c ON c.id = p.category_id WHERE p.organization_id = $1 AND c.name IN ('Almacén', 'Bebidas')", org.OrgId));

            // The provisional example product has no price anywhere; so does "Centro" (the sheet says "NO").
            Assert.Equal(0L, Scalar<long>(owner,
                "SELECT count(*) FROM price_list_entries e JOIN presentations pr ON pr.id = e.presentation_id JOIN products p ON p.id = pr.product_id WHERE p.organization_id = $1 AND p.name IN ('Milanesa de ternera de bola de lomo', 'Centro')", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CatalogSeed_CreatesTwoPriceLists_MostradorIsTheDefault_AndOnlyRepartoIsComposed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            var provisioningDefault = Scalar<Guid>(owner, "SELECT id FROM price_lists WHERE organization_id = $1 AND is_default", org.OrgId);

            ApplyCatalog(owner);

            Assert.Equal(2L, Count(owner, "price_lists", org.OrgId));
            Assert.Equal("Mostrador", Scalar<string>(owner, "SELECT name FROM price_lists WHERE organization_id = $1 AND is_default", org.OrgId));
            // The untouched empty "Default" list was renamed, not duplicated.
            Assert.Equal(provisioningDefault, ListId(owner, org.OrgId, "Mostrador"));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM price_lists WHERE organization_id = $1 AND name = 'Reparto' AND is_default", org.OrgId));

            long Entries(string list) => Scalar<long>(owner,
                "SELECT count(*) FROM price_list_entries e JOIN price_lists l ON l.id = e.price_list_id WHERE e.organization_id = $1 AND l.name = $2 AND e.effective_from = DATE '2026-10-01'", org.OrgId, list);
            Assert.Equal(ExpectedMostradorEntries, Entries("Mostrador"));
            Assert.Equal(ExpectedRepartoEntries, Entries("Reparto"));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM price_lists WHERE organization_id = $1 AND name = 'Clientes'", org.OrgId));

            // LIST-SPECIFIC rate set on Reparto: IVA, IB, Flete, Remarcacion on the base. Never an organization default.
            Assert.Equal(1L, Count(owner, "rate_component_sets", org.OrgId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM rate_component_sets WHERE organization_id = $1 AND price_list_id IS NULL", org.OrgId));
            Assert.Equal(ListId(owner, org.OrgId, "Reparto"), Scalar<Guid>(owner, "SELECT price_list_id FROM rate_component_sets WHERE organization_id = $1", org.OrgId));
            Assert.Equal(4L, Scalar<long>(owner, "SELECT count(*) FROM rate_components WHERE organization_id = $1 AND calculation_base = 'Base'", org.OrgId));
            Assert.Equal(45m, Scalar<decimal>(owner, "SELECT sum(percentage) FROM rate_components WHERE organization_id = $1", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public async Task CatalogSeed_ResolvesAsadoCompletoTo15370InReparto_AndMostradorFinalPrices_ThroughTheRealPricingPath()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        try
        {
            ApplyCatalog(owner);
            var scope = new CloudTenantScope(org.OrgId, BranchId: org.BranchId);
            var priceStore = new PostgresPriceListStore(dataSource);
            var componentStore = new PostgresRateComponentStore(dataSource);

            async Task<PriceResolutionOutcome> Resolve(string list, string product)
            {
                var listId = ListId(owner, org.OrgId, list);
                var service = new PricingResolutionService(
                    new PostgresEffectivePriceSource(priceStore, scope, listId),
                    new PostgresRateComponentSource(componentStore, scope, listId));
                return await service.ResolveAsync(PresentationId(owner, org.OrgId, product), 1m, null, ResolveOn, CancellationToken.None);
            }

            // Reparto: base 10.600 x 1.45 (IVA 10.5 + IB 2.5 + Flete 7 + Remarcacion 25, all on the base).
            var reparto = Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Reparto", "Asado completo"));
            Assert.Equal(15370m, reparto.UnitListPrice);
            Assert.Equal(16530m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Reparto", "Bola de lomo")).UnitListPrice);
            Assert.Equal(11455m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Reparto", "Sin ral")).UnitListPrice);

            // Mostrador as 003 loads it: the retail sheet's final prices, not composed (004 turns them into base prices); a wholesale-only cut has no retail price.
            Assert.Equal(18000m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Mostrador", "Bola de lomo")).UnitListPrice);
            Assert.Equal(11570m, Assert.IsType<PriceResolutionOutcome.Resolved>(await Resolve("Mostrador", "Lengua")).UnitListPrice);
            Assert.IsType<PriceResolutionOutcome.NoEffectivePrice>(await Resolve("Mostrador", "Asado completo"));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CatalogSeed_IsIdempotent_AndPreservesLaterEdits()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner);
        try
        {
            ApplyBoth(owner);
            Exec(owner, "UPDATE products SET name = 'Vacio editado' WHERE organization_id = $1 AND name = 'Vacío'", org.OrgId);
            ApplyBoth(owner);

            Assert.Equal(ExpectedCategories, Count(owner, "categories", org.OrgId));
            Assert.Equal(ExpectedProducts, Count(owner, "products", org.OrgId));
            Assert.Equal(ExpectedProducts, Count(owner, "presentations", org.OrgId));
            Assert.Equal(2L, Count(owner, "price_lists", org.OrgId));
            Assert.Equal(ExpectedMostradorEntries + ExpectedRepartoEntries, Count(owner, "price_list_entries", org.OrgId));
            Assert.Equal(1L, Count(owner, "rate_component_sets", org.OrgId));
            Assert.Equal(4L, Count(owner, "rate_components", org.OrgId));
            Assert.Equal(ExpectedSuppliers, Count(owner, "suppliers", org.OrgId));
            Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM products WHERE organization_id = $1 AND name = 'Vacio editado'", org.OrgId));
            Assert.Equal(0L, Scalar<long>(owner, "SELECT count(*) FROM products WHERE organization_id = $1 AND name = 'Vacío'", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CatalogSeed_KeepsAnExistingNonEmptyDefaultList_AndStillLoadsMostradorAsANonDefaultList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        var org = ProvisionVacaVerde(owner, withDefaultList: false);
        try
        {
            // The owner already built their own default list for the branch.
            Exec(owner,
                "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Lista del dueno', true, $4)",
                Guid.NewGuid(), org.OrgId, org.BranchId!.Value, Guid.NewGuid());

            ApplyCatalog(owner); // must not violate price_lists_one_default_per_branch

            Assert.Equal("Lista del dueno", Scalar<string>(owner, "SELECT name FROM price_lists WHERE organization_id = $1 AND is_default", org.OrgId));
            Assert.Equal(3L, Count(owner, "price_lists", org.OrgId));
            Assert.Equal(ExpectedMostradorEntries, Scalar<long>(owner,
                "SELECT count(*) FROM price_list_entries e JOIN price_lists l ON l.id = e.price_list_id WHERE e.organization_id = $1 AND l.name = 'Mostrador'", org.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }

    [Fact]
    public void CatalogSeed_IsANoOp_WithoutTheOrganization_ItsBusinessAdmin_OrItsBranch()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        using var owner = OpenOwner();
        ApplyAllMigrations(owner);
        RemoveVacaVerde(owner);
        (long, long, long) Snapshot() => (Scalar<long>(owner, "SELECT count(*) FROM products"),
            Scalar<long>(owner, "SELECT count(*) FROM categories"), Scalar<long>(owner, "SELECT count(*) FROM price_list_entries"));
        var before = Snapshot();

        ApplyCatalog(owner); // no organization: must not throw
        Assert.Equal(before, Snapshot());

        var noAdmin = ProvisionVacaVerde(owner, withBusinessAdmin: false);
        try
        {
            ApplyCatalog(owner);
            Assert.Equal(0L, Count(owner, "products", noAdmin.OrgId));
            Assert.Equal(0L, Count(owner, "categories", noAdmin.OrgId));
        }
        finally { RemoveVacaVerde(owner); }

        var noBranch = ProvisionVacaVerde(owner, withBranch: false);
        try
        {
            ApplyCatalog(owner);
            Assert.Equal(0L, Count(owner, "products", noBranch.OrgId));
            Assert.Equal(0L, Count(owner, "categories", noBranch.OrgId));
            Assert.Equal(0L, Count(owner, "price_lists", noBranch.OrgId));
        }
        finally { RemoveVacaVerde(owner); }
    }
}
