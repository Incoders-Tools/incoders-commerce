using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T3: a customer carries its price list (`priceListId` + `priceListName`). A new customer without
/// an explicit one starts on the organization's default customer list; the list must belong to the organization.
/// </summary>
[Collection("Postgres")]
public sealed class CustomerPriceListCustomerTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly NpgsqlDataSource? _dataSource;

    public CustomerPriceListCustomerTests()
    {
        if (!_postgresAvailable) return;

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }

        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose() => _dataSource?.Dispose();

    private sealed record Tenant(CloudTenantScope Scope, Guid ActorId, Guid ListA, Guid ListB);

    private static void Exec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static Tenant NewTenant()
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Exec("INSERT INTO organizations (id, name) VALUES ($1, $2)", org, "cplc " + org);
        Exec("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branch, org);
        Exec("INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Mostrador', true, $4)", a, org, branch, Guid.NewGuid());
        Exec("INSERT INTO price_lists (id, organization_id, branch_id, name, is_default, created_by_user_id) VALUES ($1, $2, $3, 'Reparto', false, $4)", b, org, branch, Guid.NewGuid());
        return new Tenant(new CloudTenantScope(org, BranchId: branch), Guid.NewGuid(), a, b);
    }

    private static NewCustomer Input(Guid id, Guid actor, Guid? priceListId = null) =>
        new(id, CustomerKind.Wholesale, "Cliente", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal, null, null,
            null, null, null, null, null, null, null, null, null, null, actor, PriceListId: priceListId);

    private static UpdateCustomer UpdateOf(CustomerRecord c, ColumnChange<Guid?>? priceList) =>
        new(c.DisplayName, c.LegalName, c.TaxIdType, c.TaxId, c.TaxCondition, c.Phone, c.Email, c.AddressStreet, c.AddressNumber,
            c.Neighborhood, c.Locality, c.Province, c.PostalCode, c.DeliveryNotes, c.DiscountPercentage, c.PaymentTerms, c.Notes,
            c.IsEnabled, PriceList: priceList);

    [Fact]
    public async Task ACustomerCreatedWithAList_CarriesItsIdAndName()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var t = NewTenant();
        var store = new PostgresCustomerStore(_dataSource!);

        var created = await store.CreateAsync(t.Scope, Input(Guid.NewGuid(), t.ActorId, t.ListB), "org-user", t.ActorId, CancellationToken.None);

        Assert.Equal(t.ListB, created.PriceListId);
        Assert.Equal("Reparto", created.PriceListName);
        Assert.Equal(t.ListB, (await store.FindAsync(t.Scope, created.Id, CancellationToken.None))!.PriceListId);
        Assert.Contains(await store.ListAsync(t.Scope, null, CancellationToken.None), c => c.Id == created.Id && c.PriceListName == "Reparto");
    }

    [Fact]
    public async Task ACustomerCreatedWithoutAList_StartsOnTheOrganizationDefaultCustomerList_WhenThereIsOne()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var t = NewTenant();
        var store = new PostgresCustomerStore(_dataSource!);

        var before = await store.CreateAsync(t.Scope, Input(Guid.NewGuid(), t.ActorId), "org-user", t.ActorId, CancellationToken.None);
        Assert.Null(before.PriceListId);

        Exec("UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", t.ListB, t.Scope.OrganizationId);
        var after = await store.CreateAsync(t.Scope, Input(Guid.NewGuid(), t.ActorId), "org-user", t.ActorId, CancellationToken.None);
        Assert.Equal(t.ListB, after.PriceListId);

        // An explicit choice wins over the organization default.
        var explicitChoice = await store.CreateAsync(t.Scope, Input(Guid.NewGuid(), t.ActorId, t.ListA), "org-user", t.ActorId, CancellationToken.None);
        Assert.Equal(t.ListA, explicitChoice.PriceListId);
    }

    [Fact]
    public async Task UpdatingACustomer_ChangesClearsOrKeepsItsList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var t = NewTenant();
        var store = new PostgresCustomerStore(_dataSource!);
        var created = await store.CreateAsync(t.Scope, Input(Guid.NewGuid(), t.ActorId, t.ListB), "org-user", t.ActorId, CancellationToken.None);

        var kept = await store.UpdateAsync(t.Scope, created.Id, UpdateOf(created, priceList: null), "org-user", t.ActorId, CancellationToken.None);
        Assert.Equal(t.ListB, kept!.PriceListId);

        var changed = await store.UpdateAsync(t.Scope, created.Id, UpdateOf(created, new ColumnChange<Guid?>(t.ListA)), "org-user", t.ActorId, CancellationToken.None);
        Assert.Equal(t.ListA, changed!.PriceListId);
        Assert.Equal("Mostrador", changed.PriceListName);

        var cleared = await store.UpdateAsync(t.Scope, created.Id, UpdateOf(created, new ColumnChange<Guid?>(null)), "org-user", t.ActorId, CancellationToken.None);
        Assert.Null(cleared!.PriceListId);
        Assert.Null(cleared.PriceListName);
    }

    [Fact]
    public async Task AListOfAnotherOrganization_IsRefused_ByTheDatabase()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var mine = NewTenant();
        var other = NewTenant();
        var store = new PostgresCustomerStore(_dataSource!);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            store.CreateAsync(mine.Scope, Input(Guid.NewGuid(), mine.ActorId, other.ListB), "org-user", mine.ActorId, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}
