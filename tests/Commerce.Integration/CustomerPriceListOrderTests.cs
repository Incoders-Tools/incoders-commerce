using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Pricing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T2: an order is priced from the list that applies to its BUYER. A registered customer (web
/// self-service or staff-entered) from the customer's own list, else the organization's default customer list, else the
/// default list; a guest from the default list. Composition then uses the rate set of THAT list. Never the channel.
/// Vaca Verde figures: Bola de lomo base 11.400, Reparto x 1,45 = 16.530, Mostrador x 1,48 = 16.872.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerPriceListOrderTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public CustomerPriceListOrderTests()
    {
        if (!_postgresAvailable) return;

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        using var reset = new NpgsqlCommand(
            """
            TRUNCATE TABLE rate_components, rate_component_sets, price_list_entries, price_lists, presentations, products,
                customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials,
                branches, organizations CASCADE
            """, owner);
        reset.ExecuteNonQuery();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private sealed record World(
        CloudTenantScope Scope, Guid ActorId, Guid PresentationId, Guid Mostrador, Guid Reparto, CloudOrderSubmissionService Service);

    private static void Exec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private async Task<Guid> NewListAsync(
        CloudTenantScope scope, Guid actorId, string name, bool isDefault, Guid presentationId, decimal basePrice, params RateComponent[] components)
    {
        var priceStore = new PostgresPriceListStore(_dataSource!);
        var list = await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actorId), "org-user", actorId, CancellationToken.None);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await priceStore.AppendEntryAsync(scope,
            new NewPriceListEntry(Guid.NewGuid(), list.Id, presentationId, basePrice, yesterday, "Manual", null, actorId), "org-user", actorId, CancellationToken.None);
        await new PostgresRateComponentStore(_dataSource!).PublishSetAsync(scope,
            new NewRateComponentSet(Guid.NewGuid(), list.Id, yesterday, components, actorId), "org-user", actorId, CancellationToken.None);
        return list.Id;
    }

    private async Task<World> NewWorldAsync()
    {
        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Org')", orgId);
        Exec("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branchId, orgId);
        var scope = new CloudTenantScope(orgId, BranchId: branchId);

        var catalog = new PostgresCatalogStore(_dataSource!);
        var product = await catalog.CreateProductAsync(scope, new NewProduct(Guid.NewGuid(), "Bola de lomo", CategoryFixture.Create(scope), Guid.NewGuid(), actorId), "org-user", actorId, CancellationToken.None);
        var presentation = await catalog.CreatePresentationAsync(scope,
            new NewPresentation(Guid.NewGuid(), product.Id, "Por kg", Commerce.Domain.Catalog.QuantityBehavior.Weighted, Guid.NewGuid(), IdentificationCode: null, actorId),
            "org-user", actorId, CancellationToken.None);

        var mostrador = await NewListAsync(scope, actorId, "Mostrador", true, presentation.Id, 11_400m,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("REMARCACION", "Remarcación (35%)", 35m, RateCalculationBase.Base, 3));
        var reparto = await NewListAsync(scope, actorId, "Reparto", false, presentation.Id, 11_400m,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new RateComponent("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4));

        var service = new CloudOrderSubmissionService(
            new CustomerCatalogAccessService(new PostgresCustomerOrderingAccessStore(_dataSource!), new InMemoryAuditSink()),
            new PostgresCustomerStore(_dataSource!), new InMemoryOrderStore(), catalog,
            new PostgresPriceListStore(_dataSource!), new PostgresRateComponentStore(_dataSource!));
        return new World(scope, actorId, presentation.Id, mostrador, reparto, service);
    }

    private async Task<Guid> NewCustomerAsync(World w, Guid? priceListId, decimal? discount = null)
    {
        var id = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(w.Scope,
            new NewCustomer(id, CustomerKind.Wholesale, "Cliente", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal, null, null,
                null, null, null, null, null, null, null, discount, null, null, w.ActorId, PriceListId: priceListId),
            "org-user", w.ActorId, CancellationToken.None);
        return id;
    }

    private static Task<OrderSubmissionOutcome> SubmitAsCustomerAsync(World w, Guid customerId, decimal quantity = 1m) =>
        w.Service.SubmitForCustomerSessionAsync(
            w.Scope, customerId, Guid.NewGuid(), Guid.NewGuid(), w.ActorId,
            [new SubmitOrderLine(Guid.NewGuid(), w.PresentationId, quantity)], Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

    [Fact]
    public async Task ARepartoCustomersWebOrder_PricesAtReparto_16530()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w, w.Reparto);

        var outcome = await SubmitAsCustomerAsync(w, customer);

        Assert.Equal(OrderSubmissionOutcomeStatus.Accepted, outcome.Status);
        Assert.Equal(16_530m, outcome.Order!.Lines[0].UnitListPrice); // 11.400 x 1,45
    }

    [Fact]
    public async Task ACustomerOnTheCounterList_PricesAtMostrador_16872_AndTheCustomersDiscountStillApplies()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w, w.Mostrador, discount: 10m);

        var line = (await SubmitAsCustomerAsync(w, customer, quantity: 2m)).Order!.Lines[0];

        Assert.Equal(16_872m, line.UnitListPrice); // 11.400 x 1,48
        Assert.Equal(15_184.80m, line.UnitNetPrice); // less 10 %
        Assert.Equal(30_369.60m, line.LineTotal);
    }

    [Fact]
    public async Task ACustomerWithoutAList_UsesTheOrganizationDefaultCustomerList_ElseTheDefaultList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w, priceListId: null);

        Assert.Equal(16_872m, (await SubmitAsCustomerAsync(w, customer)).Order!.Lines[0].UnitListPrice); // no customer default: the default list

        Exec("UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", w.Reparto, w.Scope.OrganizationId);
        Assert.Equal(16_530m, (await SubmitAsCustomerAsync(w, customer)).Order!.Lines[0].UnitListPrice);
    }

    [Fact]
    public async Task ACustomersListOfAnotherBranch_IsNotUsable_TheSellingBranchFallsBackToItsOwnDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var otherBranch = Guid.NewGuid();
        Exec("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Other')", otherBranch, w.Scope.OrganizationId);
        var otherScope = new CloudTenantScope(w.Scope.OrganizationId, BranchId: otherBranch);
        var foreign = (await new PostgresPriceListStore(_dataSource!).CreatePriceListAsync(
            otherScope, new NewPriceList(Guid.NewGuid(), "Reparto otra sucursal", false, w.ActorId), "org-user", w.ActorId, CancellationToken.None)).Id;
        var customer = await NewCustomerAsync(w, foreign);

        var outcome = await SubmitAsCustomerAsync(w, customer);

        Assert.Equal(16_872m, outcome.Order!.Lines[0].UnitListPrice); // the selling branch's default list
    }

    [Fact]
    public async Task AStaffEnteredOrder_ForACustomer_PricesFromTheCustomersList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w, w.Reparto);
        var credential = await new PostgresCustomerOrderingAccessStore(_dataSource!).IssueAsync(w.Scope, customer, w.ActorId, CancellationToken.None);

        var outcome = await w.Service.SubmitAsync(
            w.Scope, customer, credential, Guid.NewGuid(), Guid.NewGuid(), w.ActorId,
            [new SubmitOrderLine(Guid.NewGuid(), w.PresentationId, 1m)], Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.Equal(16_530m, outcome.Order!.Lines[0].UnitListPrice);
    }

    [Fact]
    public async Task AWalkInBuyer_ResolvesTheOrganizationDefaultList_EvenWhenCustomersDefaultToReparto()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        Exec("UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", w.Reparto, w.Scope.OrganizationId);
        var store = new PostgresPriceListStore(_dataSource!);

        var walkIn = await store.ResolveBuyerPriceListAsync(w.Scope, isCustomer: false, customerPriceListId: null, CancellationToken.None);
        var customer = await store.ResolveBuyerPriceListAsync(w.Scope, isCustomer: true, customerPriceListId: null, CancellationToken.None);

        Assert.Equal(w.Mostrador, walkIn!.Id);
        Assert.Equal(w.Reparto, customer!.Id);
    }
}
