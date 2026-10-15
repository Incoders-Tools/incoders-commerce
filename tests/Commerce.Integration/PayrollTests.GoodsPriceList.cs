using Commerce.Cloud.Api.Persistence;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// owner feedback 2026-10-15, B6: the staff buying goods are priced on the counter list (Mostrador, the default list), in
/// the web and at the POS. The web's quote names the list; the customer linked to an employee for its goods is on that
/// list (the organization's default customer list, e.g. Reparto, is for customers), and those linked before are moved to
/// it by migration 0060.
/// </summary>
public sealed partial class PayrollTests
{
    private const string GoodsPriceListMigration = "0060_employee_customers_on_default_list.sql";

    /// <summary>Mostrador (the default list) and Reparto, the organization's default list for new customers.</summary>
    private (Guid Mostrador, Guid Reparto) CounterAndDeliveryLists(World w)
    {
        using var owner = OpenOwner();
        var mostrador = Scalar<Guid?>(owner, "SELECT id FROM price_lists WHERE organization_id = $1 AND is_default", w.Org) ?? Guid.NewGuid();
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Mostrador', true, $4) ON CONFLICT DO NOTHING",
            mostrador, w.Org, w.Branch, w.Actor);
        var reparto = Guid.NewGuid();
        Exec(owner, "INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Reparto', false, $4)",
            reparto, w.Org, w.Branch, w.Actor);
        Exec(owner, "UPDATE organizations SET default_customer_price_list_id = $2 WHERE id = $1", w.Org, reparto);
        return (mostrador, reparto);
    }

    private static Guid? PriceListOf(Npgsql.NpgsqlConnection owner, Guid customerId) =>
        Scalar<Guid?>(owner, "SELECT price_list_id FROM customers WHERE id = $1", customerId);

    [Fact]
    public async Task TheGoodsQuote_NamesTheCounterListItPricesOn()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var g = await GoodsSeedAsync();

        var (_, quote) = await Goods().QuoteAsync(g.W.Scope, g.Juan.Id, [new(g.Asado, 1m)], CancellationToken.None);

        Assert.Equal("Mostrador", quote!.PriceListName);
    }

    [Fact]
    public async Task AnEmployeeWhoTakesGoods_GetsItsCustomerOnTheCounterList_NotTheCustomersDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var (mostrador, _) = CounterAndDeliveryLists(w);
        var employees = Employees();

        var (_, juan) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: true, CancellationToken.None);
        var (_, ana) = await employees.CreateAsync(w.Scope, w.Actor, Input(w, "Ana", 300_000m), takesGoods: false, CancellationToken.None);
        var (_, anaLater) = await employees.UpdateAsync(w.Scope, w.Actor, ana!.Id, Input(w, "Ana", 300_000m), takesGoods: true, CancellationToken.None);

        using var owner = OpenOwner();
        Assert.Equal(mostrador, PriceListOf(owner, juan!.CustomerId!.Value));
        Assert.Equal(mostrador, PriceListOf(owner, anaLater!.CustomerId!.Value));
    }

    [Fact]
    public async Task TheMigration_MovesTheCustomersLinkedToEmployeesToTheCounterList_Once_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await SeedAsync();
        var (mostrador, reparto) = CounterAndDeliveryLists(w);
        var (_, juan) = await Employees().CreateAsync(w.Scope, w.Actor, Input(w, "Juan", 500_000m), takesGoods: true, CancellationToken.None);
        var shop = Guid.NewGuid();
        using var owner = OpenOwner();
        Exec(owner, "UPDATE customers SET price_list_id = $2 WHERE id = $1", juan!.CustomerId!.Value, reparto);
        Exec(owner, "INSERT INTO customers (id, organization_id, customer_kind, display_name, created_by_user_id, price_list_id) VALUES ($1, $2, 'Wholesale', 'Almacén', $3, $4)",
            shop, w.Org, w.Actor, reparto);

        PostgresTestFixture.ApplyMigration(owner, GoodsPriceListMigration);
        PostgresTestFixture.ApplyMigration(owner, GoodsPriceListMigration);

        Assert.Equal(mostrador, PriceListOf(owner, juan.CustomerId!.Value));
        Assert.Equal(reparto, PriceListOf(owner, shop));
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", GoodsPriceListMigration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }
}
