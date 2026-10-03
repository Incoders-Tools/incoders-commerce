using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Pricing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T4, cloud side: `GET /device/pricelists/sync` (channel `price-lists`). One snapshot of what the
/// branch needs to price a sale from ANY list: every list visible to the branch with its floor, each list's effective base
/// prices, the rate component sets (list-specific and organization default, with components), each customer's list and the
/// organization default customer list. Vaca Verde figures: Bola de lomo base 11.400 (Mostrador x 1,48, Reparto x 1,45).
/// </summary>
[Collection("Postgres")]
public sealed class PriceListsReplicaSyncTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource? _dataSource;

    public PriceListsReplicaSyncTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (!_postgresAvailable) return;
        using var owner = OpenOwner();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }

        Exec(owner,
            "TRUNCATE TABLE rate_components, rate_component_sets, price_list_entries, price_lists, presentations, products, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE");
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _dataSource?.Dispose();
    }

    private sealed record World(
        Guid Org, Guid Branch, string Token, CloudTenantScope Scope, Guid Presentation, Guid Mostrador, Guid Reparto, Guid Customer);

    private async Task<World> NewWorldAsync()
    {
        var org = Guid.NewGuid();
        Guid branch;
        using (var owner = OpenOwner())
        {
            Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Org')", org);
            branch = Branch(owner, org);
        }

        var scope = new CloudTenantScope(org, BranchId: branch);
        var actor = Guid.NewGuid();
        var presentation = Presentation(org, branch, "Bola de lomo", "Por kg");
        var prices = new PostgresPriceListStore(_dataSource!);
        var sets = new PostgresRateComponentStore(_dataSource!);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        async Task<Guid> List(string name, bool isDefault, decimal basePrice, params RateComponent[] components)
        {
            var list = await prices.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actor), "org-user", actor, CancellationToken.None);
            await prices.AppendEntryAsync(scope,
                new NewPriceListEntry(Guid.NewGuid(), list.Id, presentation, basePrice, yesterday, "Manual", null, actor), "org-user", actor, CancellationToken.None);
            await sets.PublishSetAsync(scope, new NewRateComponentSet(Guid.NewGuid(), list.Id, yesterday, components, actor), "org-user", actor, CancellationToken.None);
            return list.Id;
        }

        var reparto = await List("Reparto", false, 11_400m,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new RateComponent("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4));
        var mostrador = await List("Mostrador", true, 11_400m,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("REMARCACION", "Remarcación (35%)", 35m, RateCalculationBase.Base, 3));
        await prices.SetFloorAsync(scope, mostrador, reparto, "org-user", actor, CancellationToken.None);
        await sets.PublishSetAsync(scope, new NewRateComponentSet(Guid.NewGuid(), null, yesterday,
            [new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1)], actor), "org-user", actor, CancellationToken.None);

        var customer = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(scope,
            new NewCustomer(customer, CustomerKind.Wholesale, "Carnicería Sur", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal, null, null,
                null, null, null, null, null, null, null, null, null, null, actor, PriceListId: reparto),
            "org-user", actor, CancellationToken.None);
        using (var owner = OpenOwner())
        {
            Exec(owner, "UPDATE organizations SET default_customer_price_list_id = $1 WHERE id = $2", reparto, org);
        }

        using var credentials = _factory.Services.CreateScope();
        var issued = await credentials.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(org), Guid.NewGuid(), branch, Guid.NewGuid(), CancellationToken.None);
        return new World(org, branch, issued.PlaintextToken, scope, presentation, mostrador, reparto, customer);
    }

    private async Task<PriceListsSyncResponse> PullAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/device/pricelists/sync");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PriceListsSyncResponse>())!;
    }

    [Fact]
    public async Task WithoutADeviceBearer_Is401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var response = await _factory.CreateClient().GetAsync("/device/pricelists/sync");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheSnapshot_CarriesEveryListWithItsMetadata_AndTheFloor()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var body = await PullAsync(w.Token);

        Assert.Equal(2, body.Lists.Count);
        var mostrador = Assert.Single(body.Lists, l => l.Id == w.Mostrador);
        Assert.Equal(("Mostrador", true, (Guid?)w.Reparto), (mostrador.Name, mostrador.IsDefault, mostrador.FloorPriceListId));
        var reparto = Assert.Single(body.Lists, l => l.Id == w.Reparto);
        Assert.Equal(("Reparto", false, (Guid?)null), (reparto.Name, reparto.IsDefault, reparto.FloorPriceListId));
        Assert.Equal(w.Reparto, body.OrganizationDefaultCustomerPriceListId);
    }

    [Fact]
    public async Task TheSnapshot_CarriesTheEffectiveBasePriceOfEveryList_WithItsListId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var body = await PullAsync(w.Token);

        Assert.Equal(2, body.Entries.Count);
        Assert.All(body.Entries, e => Assert.Equal((w.Presentation, 11_400m), (e.PresentationId, e.UnitPrice)));
        Assert.Equal(new[] { w.Mostrador, w.Reparto }.Order(), body.Entries.Select(e => e.PriceListId).Order());
    }

    [Fact]
    public async Task TheSnapshot_CarriesTheRateSetsOfEveryList_AndTheOrganizationDefault_WithTheirComponents()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var body = await PullAsync(w.Token);

        Assert.Equal(3, body.RateSets.Count);
        var reparto = Assert.Single(body.RateSets, s => s.PriceListId == w.Reparto);
        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], reparto.Components.Select(c => c.Code));
        Assert.Equal(25m, reparto.Components[3].Percentage);
        Assert.Equal("Base", reparto.Components[3].CalculationBase);
        var mostrador = Assert.Single(body.RateSets, s => s.PriceListId == w.Mostrador);
        Assert.Equal([10.5m, 2.5m, 35m], mostrador.Components.Select(c => c.Percentage));
        var orgDefault = Assert.Single(body.RateSets, s => s.PriceListId is null);
        Assert.Equal("IVA", Assert.Single(orgDefault.Components).Code);
    }

    [Fact]
    public async Task TheSnapshot_CarriesEachCustomersPriceList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var body = await PullAsync(w.Token);

        var assignment = Assert.Single(body.CustomerPriceLists);
        Assert.Equal((w.Customer, w.Reparto), (assignment.CustomerId, assignment.PriceListId));
    }

    [Fact]
    public async Task TheSnapshot_NeverCarriesAnotherBranchLists()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var otherBranch = Branch(OpenOwner(), w.Org, "Otra");
        var otherScope = new CloudTenantScope(w.Org, BranchId: otherBranch);
        var actor = Guid.NewGuid();
        await new PostgresPriceListStore(_dataSource!).CreatePriceListAsync(
            otherScope, new NewPriceList(Guid.NewGuid(), "Solo otra sucursal", false, actor), "org-user", actor, CancellationToken.None);

        var body = await PullAsync(w.Token);

        Assert.DoesNotContain(body.Lists, l => l.Name == "Solo otra sucursal");
    }
}
