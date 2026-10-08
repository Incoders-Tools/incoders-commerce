using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Customers;
using Commerce.Domain.Identity;
using Commerce.Domain.Ordering;
using Commerce.Domain.Pricing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// staff-order-taking T1/T2: a signed-in staff member with <see cref="Permission.TakeOrders"/> (seller, business admin,
/// or a system administrator acting on the organization) quotes and submits an order for a customer of the
/// organization. No customer credential is involved; the destination is the selected branch and the actor is the
/// caller. Prices follow the self-service rule (customer's list, fallback to the default list, customer discount).
/// Vaca Verde figures: Bola de lomo base 11.400, Reparto x 1,45 = 16.530, Mostrador x 1,48 = 16.872; Lengua is priced
/// only by Mostrador, 7.817,57 x 1,48 = 11.570.
/// </summary>
[Collection("Postgres")]
public sealed class StaffOrderTakingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private const string OrganizationSelectorHeader = "X-Organization-Id";
    private const string BranchSelectorHeader = "X-Branch-Id";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource? _dataSource;

    public StaffOrderTakingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (!_postgresAvailable) return;

        ApplyMigrationsAndReset();
        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
        _factory.Dispose();
    }

    /// <summary>Same 0009 workaround as <c>CatalogCopyEndpointTests</c>: the shared database may hold catalog rows that
    /// the org-wide 0009 indexes would reject while briefly reinstated.</summary>
    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file == "0009_catalog_and_pricing.sql")
            {
                using var truncate = new NpgsqlCommand(
                    "TRUNCATE TABLE price_import_rows, price_import_batches, supplier_price_mappings, " +
                    "price_list_entries, price_lists, presentations, products CASCADE", owner);
                try { truncate.ExecuteNonQuery(); } catch (PostgresException) { /* first run: tables do not exist yet */ }
            }

            PostgresTestFixture.ApplyMigration(owner, file);
        }

        using var reset = new NpgsqlCommand(
            """
            TRUNCATE TABLE audit_log, order_lines, orders, rate_components, rate_component_sets, price_import_rows,
                price_import_batches, supplier_price_mappings, price_list_entries, price_lists, presentations, products,
                customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials,
                branches, organizations CASCADE
            """, owner);
        reset.ExecuteNonQuery();
    }

    // --- World -----------------------------------------------------------------------------------

    private sealed record World(
        Guid OrganizationId, Guid BranchId, Guid AdminId, HttpClient Admin, CloudTenantScope Scope,
        Guid Mostrador, Guid Reparto, Guid BolaDeLomo, Guid Lengua);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static void Exec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)cmd.ExecuteScalar()!;
    }

    /// <summary>Yesterday on the business calendar the pricing reads (never the UTC date).</summary>
    private static DateOnly Yesterday => BusinessClock.System.Today.AddDays(-1);

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId, string Email)> BootstrapAsync()
    {
        var organizationId = Guid.NewGuid();
        var email = Unique("sot-admin");
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "Casa central", email, Password));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        return (organizationId, body.BranchId, body.UserId, email);
    }

    private async Task<HttpClient> SignInAsync(string email, Guid? branchId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        if (branchId is { } branch) client.DefaultRequestHeaders.Add(BranchSelectorHeader, branch.ToString());
        return client;
    }

    private async Task<Guid> NewListAsync(CloudTenantScope scope, Guid actorId, string name, bool isDefault, params RateComponent[] components)
    {
        var priceStore = new PostgresPriceListStore(_dataSource!);
        var list = await priceStore.CreatePriceListAsync(scope, new NewPriceList(Guid.NewGuid(), name, isDefault, actorId), "org-user", actorId, CancellationToken.None);
        await new PostgresRateComponentStore(_dataSource!).PublishSetAsync(scope,
            new NewRateComponentSet(Guid.NewGuid(), list.Id, Yesterday, components, actorId), "org-user", actorId, CancellationToken.None);
        return list.Id;
    }

    private async Task PriceAsync(CloudTenantScope scope, Guid actorId, Guid listId, Guid presentationId, decimal basePrice) =>
        await new PostgresPriceListStore(_dataSource!).AppendEntryAsync(scope,
            new NewPriceListEntry(Guid.NewGuid(), listId, presentationId, basePrice, Yesterday, "Manual", null, actorId), "org-user", actorId, CancellationToken.None);

    private async Task<Guid> NewPresentationAsync(CloudTenantScope scope, Guid actorId, string productName, string presentationName, string? code)
    {
        var catalog = new PostgresCatalogStore(_dataSource!);
        var product = await catalog.CreateProductAsync(scope, new NewProduct(Guid.NewGuid(), productName, CategoryFixture.Create(scope), Guid.NewGuid(), actorId), "org-user", actorId, CancellationToken.None);
        var presentation = await catalog.CreatePresentationAsync(scope,
            new NewPresentation(Guid.NewGuid(), product.Id, presentationName, Commerce.Domain.Catalog.QuantityBehavior.Weighted, Guid.NewGuid(), code, actorId),
            "org-user", actorId, CancellationToken.None);
        return presentation.Id;
    }

    private async Task<World> NewWorldAsync()
    {
        var (organizationId, branchId, adminId, adminEmail) = await BootstrapAsync();
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);

        var mostrador = await NewListAsync(scope, adminId, "Mostrador", true,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("REMARCACION", "Remarcación (35%)", 35m, RateCalculationBase.Base, 3));
        var reparto = await NewListAsync(scope, adminId, "Reparto", false,
            new RateComponent("IVA", "IVA (10,5%)", 10.5m, RateCalculationBase.Base, 1),
            new RateComponent("IB", "IB (2,5%)", 2.5m, RateCalculationBase.Base, 2),
            new RateComponent("FLETE", "Flete (7%)", 7m, RateCalculationBase.Base, 3),
            new RateComponent("REMARCACION", "Remarcación (25%)", 25m, RateCalculationBase.Base, 4));

        var bola = await NewPresentationAsync(scope, adminId, "Bola de lomo", "Por kg", "BOLA-KG");
        await PriceAsync(scope, adminId, mostrador, bola, 11_400m);
        await PriceAsync(scope, adminId, reparto, bola, 11_400m);
        var lengua = await NewPresentationAsync(scope, adminId, "Lengua", "Por kg", "LENGUA-KG");
        await PriceAsync(scope, adminId, mostrador, lengua, 7_817.57m);

        var admin = await SignInAsync(adminEmail, branchId);
        return new World(organizationId, branchId, adminId, admin, scope, mostrador, reparto, bola, lengua);
    }

    private async Task<Guid> NewCustomerAsync(
        CloudTenantScope scope, Guid actorId, Guid? priceListId, decimal? discount = null, string name = "Almacén Don Pepe",
        string? phone = null, string? taxId = null, Guid? cityId = null)
    {
        var id = Guid.NewGuid();
        await new PostgresCustomerStore(_dataSource!).CreateAsync(scope,
            new NewCustomer(id, CustomerKind.Wholesale, name, null, taxId is null ? TaxIdType.None : TaxIdType.Cuit, taxId,
                TaxCondition.ConsumidorFinal, phone, null, null, null, null, null, null, null, null, discount, null, null, actorId,
                PriceListId: priceListId, CityId: cityId),
            "org-user", actorId, CancellationToken.None);
        return id;
    }

    private async Task<(Guid UserId, HttpClient Client)> NewStaffAsync(World w, string role)
    {
        var email = Unique("sot-" + role);
        var response = await w.Admin.PostAsJsonAsync("/account/users", new CreateUserRequest(email, Password, [role], [w.BranchId]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId;
        return (userId, await SignInAsync(email, w.BranchId));
    }

    private static object Line(Guid presentationId, decimal quantity) => new { productId = Guid.NewGuid(), presentationId, quantity };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // --- T1: permission ---------------------------------------------------------------------------

    [Fact]
    public async Task ASeller_SeesTakeOrdersInAccountMe()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);

        var me = await JsonAsync(await seller.GetAsync("/account/me"));

        Assert.True(((Permission)me.GetProperty("permissions").GetInt32()).HasFlag(Permission.TakeOrders));
    }

    // --- T2: quote --------------------------------------------------------------------------------

    [Fact]
    public async Task Quote_ForARepartoCustomer_PricesAtReparto_AndFallsBackToMostradorWithTheDiscount()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, discount: 10m);

        var response = await seller.PostAsJsonAsync("/orders/staff/quote",
            new { customerId = customer, lines = new[] { Line(w.BolaDeLomo, 2m), Line(w.Lengua, 1m) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var quote = await JsonAsync(response);
        Assert.Equal("quoted", quote.GetProperty("status").GetString());
        Assert.Equal("Reparto", quote.GetProperty("priceListName").GetString());
        Assert.Equal(10m, quote.GetProperty("discountPercentage").GetDecimal());

        var bola = quote.GetProperty("lines")[0];
        Assert.Equal("priced", bola.GetProperty("status").GetString());
        Assert.Equal("Bola de lomo", bola.GetProperty("productName").GetString());
        Assert.Equal(16_530m, bola.GetProperty("unitListPrice").GetDecimal());
        Assert.Equal(14_877m, bola.GetProperty("unitNetPrice").GetDecimal());      // less 10 %
        Assert.Equal(29_754m, bola.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(w.Reparto, bola.GetProperty("priceListId").GetGuid());
        Assert.Equal("Reparto", bola.GetProperty("priceListName").GetString());
        Assert.False(bola.GetProperty("fellBack").GetBoolean());

        var lengua = quote.GetProperty("lines")[1];
        Assert.Equal(11_570m, Math.Round(lengua.GetProperty("unitListPrice").GetDecimal(), 2));
        Assert.Equal(10_413m, lengua.GetProperty("unitNetPrice").GetDecimal());
        Assert.Equal(w.Mostrador, lengua.GetProperty("priceListId").GetGuid());
        Assert.Equal("Mostrador", lengua.GetProperty("priceListName").GetString());
        Assert.True(lengua.GetProperty("fellBack").GetBoolean());

        Assert.Equal(29_754m + 10_413m, quote.GetProperty("total").GetDecimal());
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM orders WHERE organization_id = $1", w.OrganizationId));
    }

    [Fact]
    public async Task Quote_WithAnUnpricedLine_IsAnUnprocessableDenial_NamingTheLine()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);

        var response = await seller.PostAsJsonAsync("/orders/staff/quote",
            new { customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m), Line(Guid.NewGuid(), 1m) } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var quote = await JsonAsync(response);
        Assert.Equal("denied", quote.GetProperty("status").GetString());
        Assert.Equal("no-effective-price", quote.GetProperty("reason").GetString());
        Assert.Equal("priced", quote.GetProperty("lines")[0].GetProperty("status").GetString());
        Assert.Equal("no-effective-price", quote.GetProperty("lines")[1].GetProperty("status").GetString());
    }

    // --- T2: submit -------------------------------------------------------------------------------

    [Fact]
    public async Task ASeller_SubmitsAnOrderForARepartoCustomer_PricedAtReparto_ToTheSelectedBranch_AsTheActor()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (sellerId, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var orderId = Guid.NewGuid();

        var response = await seller.PostAsJsonAsync("/orders/staff",
            new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) }, note = "Entregar por la tarde" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await JsonAsync(response);
        Assert.Equal("accepted", outcome.GetProperty("status").GetString());
        Assert.Equal("accepted", outcome.GetProperty("reason").GetString());
        Assert.True(OrderNumber.TryParse(outcome.GetProperty("orderNumber").GetString(), out _));
        var order = outcome.GetProperty("order");
        Assert.Equal(orderId, order.GetProperty("orderId").GetGuid());
        Assert.Equal(w.BranchId, order.GetProperty("destinationBranchId").GetGuid());
        Assert.Equal(customer, order.GetProperty("customerId").GetGuid());
        Assert.Equal(sellerId, order.GetProperty("takenByUserId").GetGuid());
        Assert.Equal("Entregar por la tarde", order.GetProperty("note").GetString());
        Assert.Equal(16_530m, order.GetProperty("lines")[0].GetProperty("unitListPrice").GetDecimal());

        Assert.Equal(16_530m, Scalar<decimal>("SELECT unit_list_price FROM order_lines WHERE order_id = $1", orderId));
        Assert.Equal(sellerId, Scalar<Guid>("SELECT taken_by_user_id FROM orders WHERE order_id = $1", orderId));
        Assert.Equal(1L, Scalar<long>(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'order.staff-submitted' AND actor_id = $2 AND actor_kind = 'org-user'",
            orderId, sellerId));
    }

    [Fact]
    public async Task ASubmittedStaffOrder_AppearsInThePendingList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var orderId = Guid.NewGuid();

        var submit = await w.Admin.PostAsJsonAsync("/orders/staff", new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 3m) } });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var pending = await JsonAsync(await w.Admin.GetAsync("/orders/pending"));
        Assert.Contains(pending.EnumerateArray(), o => o.GetProperty("orderId").GetGuid() == orderId);
    }

    [Fact]
    public async Task SubmittingTheSameOrderIdTwice_CreatesOneOrder_WithTheSameNumber()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var request = new { orderId = Guid.NewGuid(), customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } };

        var first = await JsonAsync(await seller.PostAsJsonAsync("/orders/staff", request));
        var secondResponse = await seller.PostAsJsonAsync("/orders/staff", request);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await JsonAsync(secondResponse);
        Assert.Equal("existing-order", second.GetProperty("reason").GetString());
        Assert.Equal(first.GetProperty("orderNumber").GetString(), second.GetProperty("orderNumber").GetString());
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM orders WHERE order_id = $1", request.orderId));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'order.staff-submitted'", request.orderId));
    }

    [Fact]
    public async Task ReusingAnOrderIdForAnotherCustomer_IsAConflict_ThatRevealsNothingOfTheStoredOrder()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var otherCustomer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, name: "Carnicería La Otra");
        var orderId = Guid.NewGuid();

        var first = await seller.PostAsJsonAsync("/orders/staff", new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var reused = await seller.PostAsJsonAsync("/orders/staff", new { orderId, customerId = otherCustomer, lines = new[] { Line(w.Lengua, 2m) } });

        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var body = await JsonAsync(reused);
        Assert.Equal("denied", body.GetProperty("status").GetString());
        Assert.Equal("order-id-conflict", body.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("order").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orderNumber").ValueKind);
        Assert.Equal(customer, Scalar<Guid>("SELECT customer_id FROM orders WHERE order_id = $1", orderId));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM orders WHERE order_id = $1", orderId));

        // The original customer's replay still returns the stored order.
        var replay = await seller.PostAsJsonAsync("/orders/staff", new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("existing-order", (await JsonAsync(replay)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ACashier_IsRefusedEveryStaffOrderRoute()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, cashier) = await NewStaffAsync(w, RoleCatalog.Cashier);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var lines = new[] { Line(w.BolaDeLomo, 1m) };

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/orders/staff/quote", new { customerId = customer, lines })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/orders/staff", new { orderId = Guid.NewGuid(), customerId = customer, lines })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/orders/staff/customers?search=pepe")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/orders/staff/presentations?search=lomo")).StatusCode);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM orders WHERE organization_id = $1", w.OrganizationId));
    }

    [Fact]
    public async Task WithoutASelectedBranch_TheSubmissionIsBranchSelectionRequired()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        seller.DefaultRequestHeaders.Remove(BranchSelectorHeader);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);

        var response = await seller.PostAsJsonAsync("/orders/staff", new { orderId = Guid.NewGuid(), customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("branch-selection-required", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ASystemAdministratorActingOnTheOrganization_MayTakeAnOrder()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        var (_, _, sysadminId, sysadminEmail) = await BootstrapAsync();
        Exec("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE id = $1", sysadminId);
        var sysadmin = await SignInAsync(sysadminEmail, branchId: null);
        sysadmin.DefaultRequestHeaders.Add(OrganizationSelectorHeader, w.OrganizationId.ToString());
        sysadmin.DefaultRequestHeaders.Add(BranchSelectorHeader, w.BranchId.ToString());
        var orderId = Guid.NewGuid();

        var response = await sysadmin.PostAsJsonAsync("/orders/staff", new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sysadminId, (await JsonAsync(response)).GetProperty("order").GetProperty("takenByUserId").GetGuid());
    }

    [Fact]
    public async Task ACustomerOfAnotherOrganization_IsNotFound()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var (otherOrg, otherBranch, otherAdmin, _) = await BootstrapAsync();
        var foreign = await NewCustomerAsync(new CloudTenantScope(otherOrg, BranchId: otherBranch), otherAdmin, priceListId: null);

        var quote = await seller.PostAsJsonAsync("/orders/staff/quote", new { customerId = foreign, lines = new[] { Line(w.BolaDeLomo, 1m) } });
        var submit = await seller.PostAsJsonAsync("/orders/staff", new { orderId = Guid.NewGuid(), customerId = foreign, lines = new[] { Line(w.BolaDeLomo, 1m) } });

        Assert.Equal(HttpStatusCode.NotFound, quote.StatusCode);
        Assert.Equal("not-found", (await JsonAsync(quote)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotFound, submit.StatusCode);
        Assert.Equal("not-found", (await JsonAsync(submit)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ADisabledCustomer_IsDenied()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);
        Exec("UPDATE customers SET is_enabled = false WHERE id = $1", customer);

        var submit = await seller.PostAsJsonAsync("/orders/staff", new { orderId = Guid.NewGuid(), customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } });

        Assert.Equal(HttpStatusCode.Conflict, submit.StatusCode);
        Assert.Equal("customer-disabled", (await JsonAsync(submit)).GetProperty("reason").GetString());
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM orders WHERE organization_id = $1", w.OrganizationId));
    }

    [Fact]
    public async Task InvalidRequests_AreValidationProblems()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto);

        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/orders/staff",
            new { orderId = Guid.NewGuid(), customerId = customer, lines = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/orders/staff",
            new { orderId = Guid.NewGuid(), customerId = customer, lines = new[] { Line(w.BolaDeLomo, 0m) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/orders/staff",
            new { orderId = Guid.Empty, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync("/orders/staff",
            new { orderId = Guid.NewGuid(), customerId = customer, lines = new[] { Line(w.BolaDeLomo, 1m) }, note = new string('x', 501) })).StatusCode);
    }

    // --- T2: lookups for the screen ----------------------------------------------------------------

    [Fact]
    public async Task ASeller_FindsCustomersByNamePhoneOrTaxId_WithCityAndPriceList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var cityId = Scalar<Guid>("SELECT id FROM cities ORDER BY name LIMIT 1");
        var cityName = Scalar<string>("SELECT name FROM cities WHERE id = $1", cityId);
        var pepe = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, name: "Almacén Don Pepe", phone: "11 4567-8901", taxId: "20123456786", cityId: cityId);
        await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, name: "Carnicería Rosa", phone: "11 0000-0000");

        foreach (var search in new[] { "almacen", "4567-8901", "20-12345678-6" })
        {
            var found = await JsonAsync(await seller.GetAsync($"/orders/staff/customers?search={Uri.EscapeDataString(search)}"));
            var only = Assert.Single(found.EnumerateArray());
            Assert.Equal(pepe, only.GetProperty("id").GetGuid());
            Assert.Equal("Almacén Don Pepe", only.GetProperty("displayName").GetString());
            Assert.Equal("20123456786", only.GetProperty("taxId").GetString());
            Assert.Equal("11 4567-8901", only.GetProperty("phone").GetString());
            Assert.Equal(cityName, only.GetProperty("cityName").GetString());
            Assert.Equal(w.Reparto, only.GetProperty("priceListId").GetGuid());
            Assert.Equal("Reparto", only.GetProperty("priceListName").GetString());
            Assert.True(only.GetProperty("isEnabled").GetBoolean());
        }
    }

    [Fact]
    public async Task ASeller_FindsActivePresentationsByNameOrCode()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);

        var byName = await JsonAsync(await seller.GetAsync("/orders/staff/presentations?search=lomo"));
        var bola = Assert.Single(byName.EnumerateArray());
        Assert.Equal(w.BolaDeLomo, bola.GetProperty("presentationId").GetGuid());
        Assert.Equal("Bola de lomo", bola.GetProperty("productName").GetString());
        Assert.Equal("Por kg", bola.GetProperty("presentationName").GetString());
        Assert.Equal("BOLA-KG", bola.GetProperty("identificationCode").GetString());
        Assert.Equal("Weighted", bola.GetProperty("quantityBehavior").GetString());
        Assert.NotEqual(Guid.Empty, bola.GetProperty("productId").GetGuid());

        var byCode = await JsonAsync(await seller.GetAsync("/orders/staff/presentations?search=lengua-kg"));
        Assert.Equal(w.Lengua, Assert.Single(byCode.EnumerateArray()).GetProperty("presentationId").GetGuid());

        Exec("UPDATE products SET is_active = false WHERE id = (SELECT product_id FROM presentations WHERE id = $1)", w.Lengua);
        Assert.Empty((await JsonAsync(await seller.GetAsync("/orders/staff/presentations?search=lengua"))).EnumerateArray());
    }

    // --- order-fulfillment-and-delivery: tracking, runs, remitos, settlement over HTTP ------------

    [Fact]
    public async Task ASeller_FollowsAnOrderFromTakingItToItsDelivery_AndACashierCannot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var (_, cashier) = await NewStaffAsync(w, RoleCatalog.Cashier);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, name: "Parrilla Don Julio");
        var orderId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await seller.PostAsJsonAsync("/orders/staff",
            new { orderId, customerId = customer, lines = new[] { Line(w.BolaDeLomo, 2m) } })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/orders/tracking")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/deliveries/runs")).StatusCode);

        var listed = await JsonAsync(await seller.GetAsync("/orders/tracking?status=Active&search=don%20julio"));
        var summary = Assert.Single(listed.EnumerateArray());
        Assert.Equal(("Confirmed", 33_060m), (summary.GetProperty("status").GetString(), summary.GetProperty("total").GetDecimal()));

        Assert.Equal(HttpStatusCode.NoContent,
            (await seller.PostAsJsonAsync($"/orders/tracking/{orderId}/status", new { status = "InPreparation" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await seller.PostAsJsonAsync($"/orders/tracking/{orderId}/status", new { status = "Delivered" })).StatusCode);

        var created = await seller.PostAsJsonAsync("/deliveries/runs",
            new { runDate = DateOnly.FromDateTime(DateTime.Today), driverName = "Juan", vehicle = "Kangoo", orderIds = new[] { orderId } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var runId = (await JsonAsync(created)).GetProperty("runId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await seller.PostAsync($"/deliveries/runs/{runId}/dispatch", null)).StatusCode);

        var remitos = await JsonAsync(await seller.PostAsJsonAsync("/orders/tracking/remitos", new { orderIds = new[] { orderId } }));
        var remito = Assert.Single(remitos.EnumerateArray());
        Assert.StartsWith("R", remito.GetProperty("remitoNumber").GetString());
        Assert.Equal("Parrilla Don Julio", remito.GetProperty("customer").GetProperty("displayName").GetString());

        var settle = await seller.PostAsJsonAsync($"/deliveries/runs/{runId}/settle", new
        {
            orders = new[] { new { orderId, delivered = true, settlement = "CurrentAccount", lines = new[] { new { lineNo = 1, deliveredQuantity = 2.1m } } } },
        });
        Assert.Equal(HttpStatusCode.NoContent, settle.StatusCode);

        var detail = await JsonAsync(await seller.GetAsync($"/orders/tracking/{orderId}"));
        Assert.Equal("Delivered", detail.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(34_713m, detail.GetProperty("summary").GetProperty("deliveredTotal").GetDecimal()); // 16.530 x 2,1
        Assert.Equal(34_713m, Scalar<decimal>(
            "SELECT amount FROM current_account_movements WHERE customer_id = $1 AND source_id = $2", customer, orderId));
    }

    [Fact]
    public async Task TheDocumentData_IsReadByStaff_ButOnlyWhoManagesTheBranchSettingsChangesIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var body = new { legalName = "Distribuidora Arrecifes S.R.L.", taxId = "30-71234567-1", taxCondition = "ResponsableInscripto", primaryColor = "#2f7d32" };

        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PutAsJsonAsync("/account/organization/document-profile", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await w.Admin.PutAsJsonAsync("/account/organization/document-profile", body with { taxId = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.PutAsJsonAsync("/account/organization/document-profile", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.PutAsJsonAsync($"/account/branch-profiles/{w.BranchId}",
            new { address = "Frascheri 626", locality = "Arrecifes", warehouseAddress = "Depósito Ruta 51" })).StatusCode);

        var profile = await JsonAsync(await seller.GetAsync("/account/organization/document-profile"));
        Assert.Equal(("Distribuidora Arrecifes S.R.L.", "30712345671"),
            (profile.GetProperty("legalName").GetString(), profile.GetProperty("taxId").GetString()));
        var branch = Assert.Single((await JsonAsync(await seller.GetAsync("/account/branch-profiles"))).EnumerateArray());
        Assert.Equal("Depósito Ruta 51", branch.GetProperty("warehouseAddress").GetString());
    }

    // --- customer current account over HTTP ------------------------------------------------------

    [Fact]
    public async Task TheCustomerAccount_ChargesADeliveryOnAccount_TakesAPayment_AndOnlyAdminsManageIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var (_, seller) = await NewStaffAsync(w, RoleCatalog.Seller);
        var customer = await NewCustomerAsync(w.Scope, w.AdminId, w.Reparto, name: "Parrilla Don Julio");
        var opening = await w.Admin.PostAsJsonAsync($"/customers/{customer}/account/movements",
            new { kind = "OpeningBalance", amount = 50_000m, concept = "Saldo anterior" });
        Assert.Equal(HttpStatusCode.Created, opening.StatusCode);
        var openingBody = await JsonAsync(opening);
        Assert.Equal("Debit", openingBody.GetProperty("direction").GetString());
        Assert.Equal(customer, openingBody.GetProperty("customerId").GetGuid());

        var payment = await w.Admin.PostAsJsonAsync($"/customers/{customer}/account/movements",
            new { kind = "Payment", amount = 20_000m, concept = "Cobro en efectivo", documentReference = "Recibo 1" });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PostAsJsonAsync($"/customers/{customer}/account/movements",
            new { kind = "Invoice", amount = 1m, concept = "x", direction = "Credit" })).StatusCode);

        var summary = await JsonAsync(await w.Admin.GetAsync($"/customers/{customer}/account/summary"));
        Assert.Equal(30_000m, summary.GetProperty("balance").GetDecimal()); // what the customer owes

        var statement = await JsonAsync(await w.Admin.GetAsync($"/customers/{customer}/account/statement"));
        Assert.Equal([50_000m, 30_000m],
            statement.GetProperty("movements").EnumerateArray().Select(m => m.GetProperty("runningBalance").GetDecimal()));

        var paymentId = (await JsonAsync(payment)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created,
            (await w.Admin.PostAsJsonAsync($"/customers/{customer}/account/movements/{paymentId}/reverse", new { })).StatusCode);
        var balances = await JsonAsync(await w.Admin.GetAsync("/customers/account/balances"));
        var row = Assert.Single(balances.EnumerateArray());
        Assert.Equal((customer, 50_000m), (row.GetProperty("customerId").GetGuid(), row.GetProperty("balance").GetDecimal()));

        Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync($"/customers/{customer}/account/summary")).StatusCode);
    }
}
