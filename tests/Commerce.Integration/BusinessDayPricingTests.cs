using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Commerce.Domain.Pricing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists L3 in the cloud: with the clock at 22:30 in Argentina on 2026-10-02 (01:30 UTC on 10-03) an entry
/// effective "tomorrow" (10-03) must NOT apply yet: not to the POS replica, not to the default date of the breakdown and
/// not to an order submitted at that moment. Mostrador base 11.400 from 2026-09-01 (x 1,48 = 16.872); tomorrow's entry 20.000.
/// </summary>
[Collection("Postgres")]
public sealed class BusinessDayPricingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "test-password";
    private static readonly DateOnly Yesterday = new(2026, 9, 1);
    private static readonly DateOnly Tomorrow = new(2026, 10, 3);
    private static readonly IBusinessClock Clock = new BusinessClock(new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 1, 30, 0, TimeSpan.Zero)));

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource? _dataSource;

    public BusinessDayPricingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString)
                .ConfigureServices(services => services.AddSingleton(Clock)));
        if (!_postgresAvailable) return;
        using var owner = OpenOwner();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }

        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _dataSource?.Dispose();
    }

    private sealed record World(Guid Org, Guid Branch, CloudTenantScope Scope, Guid Actor, Guid Presentation, Guid Mostrador, Guid Customer);

    private async Task<World> NewWorldAsync()
    {
        var org = Guid.NewGuid();
        Guid branch;
        using (var owner = OpenOwner())
        {
            Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Org')", org);
            branch = Guid.NewGuid();
            Exec(owner, "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branch, org);
        }

        var scope = new CloudTenantScope(org, BranchId: branch);
        var actor = Guid.NewGuid();
        var presentation = Presentation(org, branch, "Bola de lomo", "Por kg");
        var prices = new PostgresPriceListStore(_dataSource!);
        var list = await prices.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), "Mostrador", true, actor), "org-user", actor, CancellationToken.None);
        foreach (var (from, price) in new[] { (Yesterday, 11_400m), (Tomorrow, 20_000m) })
        {
            await prices.AppendEntryAsync(scope,
                new NewPriceListEntry(Guid.NewGuid(), list.Id, presentation, price, from, "Manual", null, actor), "org-user", actor, CancellationToken.None);
        }

        await new PostgresRateComponentStore(_dataSource!).PublishSetAsync(scope, new NewRateComponentSet(Guid.NewGuid(), list.Id, Yesterday,
            [
                new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
                new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
                new RateComponent("REMARCACION", "Remarcación (35%)", 35m, RateCalculationBase.Base, 3),
            ], actor), "org-user", actor, CancellationToken.None);

        var customer = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(scope,
            new NewCustomer(customer, CustomerKind.Wholesale, "Cliente", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal, null, null,
                null, null, null, null, null, null, null, null, null, null, actor, PriceListId: list.Id),
            "org-user", actor, CancellationToken.None);
        return new World(org, branch, scope, actor, presentation, list.Id, customer);
    }

    [Fact]
    public async Task TheReplica_At2230InArgentina_SendsTodaysEntries_NotTomorrows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        using var credentials = _factory.Services.CreateScope();
        var issued = await credentials.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(w.Org), Guid.NewGuid(), w.Branch, Guid.NewGuid(), CancellationToken.None);
        var request = new HttpRequestMessage(HttpMethod.Get, "/device/pricelists/sync");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PriceListsSyncResponse>())!;
        var entry = Assert.Single(body.Entries);
        Assert.Equal((11_400m, Yesterday), (entry.UnitPrice, entry.EffectiveFrom));
    }

    [Fact]
    public async Task TheReplica_SendsTheEnabledCustomersOwnDiscounts_AndNoZeroOrNullOne()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync(); // its customer has no discount
        var discounted = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(w.Scope,
            new NewCustomer(discounted, CustomerKind.Wholesale, "Con descuento", null, TaxIdType.None, null, TaxCondition.ConsumidorFinal, null, null,
                null, null, null, null, null, null, null, 12.5m, null, null, w.Actor),
            "org-user", w.Actor, CancellationToken.None);
        using var credentials = _factory.Services.CreateScope();
        var issued = await credentials.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(w.Org), Guid.NewGuid(), w.Branch, Guid.NewGuid(), CancellationToken.None);
        var request = new HttpRequestMessage(HttpMethod.Get, "/device/pricelists/sync");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<PriceListsSyncResponse>())!;
        var discount = Assert.Single(body.CustomerDiscounts!);
        Assert.Equal((discounted, 12.5m), (discount.CustomerId, discount.DiscountPercentage));

        // The terms of sales on account: the customers with their own, and the organization's default for the rest.
        Assert.Empty(body.CustomerPaymentTerms!);
        // The categories travel too, with whether the POS rail offers each one.
        Assert.NotNull(body.Categories);
        Assert.All(body.Categories!, category => Assert.True(category.ShowInPos));
        Assert.Equal(30, body.DefaultCustomerPaymentTermsDays);
        Assert.Empty(body.CustomerBalances!); // nobody owes anything yet
    }

    [Fact]
    public async Task TheBreakdownWithoutADate_At2230InArgentina_ShowsTodaysBase_NotTomorrows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var admin = await SignedInAsync(w);

        var response = await admin.GetAsync($"/pricing/price-lists/{w.Mostrador}/breakdown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(11_400m, item.GetProperty("base").GetDecimal());
        Assert.Equal(16_872m, item.GetProperty("final").GetDecimal());
    }

    [Fact]
    public async Task AnOrderSubmitted_At2230InArgentina_IsPricedWithTodaysEntry_NotTomorrows()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var service = new CloudOrderSubmissionService(
            new CustomerCatalogAccessService(new PostgresCustomerOrderingAccessStore(_dataSource!), new InMemoryAuditSink()),
            new PostgresCustomerStore(_dataSource!), new InMemoryOrderStore(), new PostgresCatalogStore(_dataSource!),
            new PostgresPriceListStore(_dataSource!), new PostgresRateComponentStore(_dataSource!), businessClock: Clock);

        var outcome = await service.SubmitForCustomerSessionAsync(
            w.Scope, w.Customer, Guid.NewGuid(), Guid.NewGuid(), w.Actor,
            [new SubmitOrderLine(Guid.NewGuid(), w.Presentation, 1m)], Guid.NewGuid(), destination: null, hasAvailableStock: true, CancellationToken.None);

        Assert.True(outcome.Order is not null, outcome.Reason);
        Assert.Equal(16_872m, outcome.Order!.Lines[0].UnitNetPrice); // 11.400 x 1,48, not 20.000 x 1,48
    }

    private async Task<HttpClient> SignedInAsync(World w)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var user = new NewUserAccount(
                userId, email, hasher.HashPassword(new UserAccount(userId, w.Org, [], []), Password),
                new[] { w.Branch }, new[] { new RoleDto("test-role", Permission.ManageCatalog | Permission.ManageUsers) });
            Assert.Equal(CreateStaffUserOutcome.Created, await store.CreateStaffUserAsync(
                new CloudTenantScope(w.Org), user,
                new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry("org-user", Guid.NewGuid(), w.Org, "user", userId, "user.created", null, null),
                CancellationToken.None));
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, w.Branch.ToString());
        return client;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
