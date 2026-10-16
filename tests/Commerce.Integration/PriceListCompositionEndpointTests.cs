using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T3: `GET /pricing/price-lists/{id}/breakdown`, `POST .../copy`, `POST .../composition`,
/// `PUT .../floor` and the floor rule (409 `price-below-floor` with the violating products). Vaca Verde figures: Bola de
/// lomo base 11.400, Reparto x 1,45 = 16.530, Mostrador x 1,48 = 16.872, Mostrador copy with Remarcación 40 = x 1,53.
/// </summary>
[Collection("Postgres")]
public sealed class PriceListCompositionEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "test-password";
    // The cookie scheme answers a forbidden caller with a redirect to its access-denied path (or 403 behind a proxy).
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.Found];
    private static readonly string[] Dates = ["2026-10-01", "2026-10-02", "2026-10-03"];

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PriceListCompositionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            foreach (var file in Directory.GetFiles(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations"), "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
        }
    }

    public void Dispose() => _factory.Dispose();

    private sealed record World(
        HttpClient Admin, Guid OrganizationId, Guid BranchId, Guid Bola, Guid Lengua, Guid Reparto, Guid Mostrador);

    private static void Exec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private async Task<HttpClient> SignedInAsync(Guid organizationId, Guid branchId, Permission permissions)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var user = new NewUserAccount(
                userId, email, hasher.HashPassword(new UserAccount(userId, organizationId, [], []), Password),
                new[] { branchId }, new[] { new RoleDto("test-role", permissions) });
            var outcome = await store.CreateStaffUserAsync(
                new CloudTenantScope(organizationId), user,
                new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId, "user", userId, "user.created", null, null),
                CancellationToken.None);
            Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branchId.ToString());
        return client;
    }

    private static object[] Components(decimal remarcacion, bool flete) => flete
        ?
        [
            new { code = "IVA", label = "IVA (10,5%)", percentage = 10.5m, calculationBase = "Base", order = 1 },
            new { code = "IB", label = "Ingresos Brutos (2,5%)", percentage = 2.5m, calculationBase = "Base", order = 2 },
            new { code = "FLETE", label = "Flete (7%)", percentage = 7m, calculationBase = "Base", order = 3 },
            new { code = "REMARCACION", label = $"Remarcación ({remarcacion}%)", percentage = remarcacion, calculationBase = "Base", order = 4 },
        ]
        :
        [
            new { code = "IVA", label = "IVA (10,5%)", percentage = 10.5m, calculationBase = "Base", order = 1 },
            new { code = "IB", label = "Ingresos Brutos (2,5%)", percentage = 2.5m, calculationBase = "Base", order = 2 },
            new { code = "REMARCACION", label = $"Remarcación ({remarcacion}%)", percentage = remarcacion, calculationBase = "Base", order = 3 },
        ];

    private async Task<Guid> CreateListAsync(HttpClient admin, string name, bool isDefault, decimal bola, decimal? lengua, object[] components)
    {
        var created = await admin.PostAsJsonAsync("/pricing/price-lists", new { name, isDefault });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return id;
    }

    private async Task<World> NewWorldAsync(bool withFloor = true)
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        Exec("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", organizationId);
        Exec("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Main')", branchId, organizationId);
        var scope = new CloudTenantScope(organizationId, BranchId: branchId);
        var admin = await SignedInAsync(organizationId, branchId, Permission.ManageCatalog | Permission.ManageUsers);

        Guid bola, lengua;
        using (var s = _factory.Services.CreateScope())
        {
            var catalog = s.ServiceProvider.GetRequiredService<PostgresCatalogStore>();
            var actor = Guid.NewGuid();
            async Task<Guid> Presentation(string name)
            {
                var product = await catalog.CreateProductAsync(scope, new NewProduct(Guid.NewGuid(), name, CategoryFixture.Create(scope), Guid.NewGuid(), actor), "org-user", actor, CancellationToken.None);
                return (await catalog.CreatePresentationAsync(scope,
                    new NewPresentation(Guid.NewGuid(), product.Id, "Por kg", QuantityBehavior.Weighted, Guid.NewGuid(), null, actor), "org-user", actor, CancellationToken.None)).Id;
            }

            bola = await Presentation("Bola de lomo");
            lengua = await Presentation("Lengua");
        }

        var mostrador = await CreateListAsync(admin, "Mostrador", true, 0, null, []);
        var reparto = await CreateListAsync(admin, "Reparto", false, 0, null, []);

        async Task Price(Guid list, Guid presentation, decimal price) =>
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/pricing/price-lists/{list}/entries", new { presentationId = presentation, unitPrice = price, effectiveFrom = Dates[0] })).StatusCode);
        async Task Composition(Guid list, object[] components) =>
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/pricing/price-lists/{list}/composition", new { effectiveFrom = Dates[0], components })).StatusCode);

        await Price(reparto, bola, 11_400m);
        await Composition(reparto, Components(25m, flete: true));
        await Price(mostrador, bola, 11_400m);
        await Price(mostrador, lengua, 7_817.57m); // counter-only cut
        await Composition(mostrador, Components(35m, flete: false));
        if (withFloor)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/pricing/price-lists/{mostrador}/floor", new { floorPriceListId = reparto })).StatusCode);
        }

        return new World(admin, organizationId, branchId, bola, lengua, reparto, mostrador);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static JsonElement Item(JsonElement breakdown, Guid presentationId) =>
        breakdown.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("presentationId").GetGuid() == presentationId);

    private async Task<JsonElement> BreakdownAsync(World w, Guid list, string on)
    {
        var response = await w.Admin.GetAsync($"/pricing/price-lists/{list}/breakdown?on={on}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    // ------------------------------------------------------------------ breakdown

    [Fact]
    public async Task Breakdown_ShowsBase_EachComponentWithItsAmount_AndTheFinalPrice_ForEveryProduct()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var reparto = await BreakdownAsync(w, w.Reparto, Dates[1]);
        var bola = Item(reparto, w.Bola);
        Assert.Equal("Bola de lomo", bola.GetProperty("productName").GetString());
        Assert.Equal(11_400m, bola.GetProperty("base").GetDecimal());
        Assert.Equal(16_530m, bola.GetProperty("final").GetDecimal());
        var lines = bola.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(["IVA", "IB", "FLETE", "REMARCACION"], lines.Select(l => l.GetProperty("code").GetString()));
        Assert.Equal([10.5m, 2.5m, 7m, 25m], lines.Select(l => l.GetProperty("percentage").GetDecimal()));
        Assert.Equal([1_197m, 285m, 798m, 2_850m], lines.Select(l => l.GetProperty("amount").GetDecimal()));
        Assert.All(lines, l => Assert.Equal(11_400m, l.GetProperty("calculationAmount").GetDecimal()));
        Assert.Equal(1, reparto.GetProperty("items").GetArrayLength());
        Assert.Equal("list", reparto.GetProperty("composition").GetProperty("source").GetString());

        var mostrador = await BreakdownAsync(w, w.Mostrador, Dates[1]);
        Assert.Equal(16_872m, Item(mostrador, w.Bola).GetProperty("final").GetDecimal()); // 11.400 x 1,48
        Assert.Equal(3, Item(mostrador, w.Bola).GetProperty("components").GetArrayLength()); // no flete
        Assert.Equal(7_817.57m, Item(mostrador, w.Lengua).GetProperty("base").GetDecimal());
        Assert.Equal(11_570m, Item(mostrador, w.Lengua).GetProperty("final").GetDecimal()); // to the cent
        Assert.Equal(w.Reparto, mostrador.GetProperty("floorPriceListId").GetGuid());
    }

    [Fact]
    public async Task Breakdown_OfAListWithoutAnyComposition_IsJustTheBase_AndOfAnUnknownList_Is404()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var bare = await CreateListAsync(w.Admin, "Sin tasas", false, 0, null, []);
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{bare}/entries", new { presentationId = w.Bola, unitPrice = 100m, effectiveFrom = Dates[0] })).StatusCode);

        var breakdown = await BreakdownAsync(w, bare, Dates[1]);

        Assert.Equal("none", breakdown.GetProperty("composition").GetProperty("source").GetString());
        Assert.Equal(100m, Item(breakdown, w.Bola).GetProperty("final").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.GetAsync($"/pricing/price-lists/{Guid.NewGuid()}/breakdown")).StatusCode);
    }

    // ----------------------------------------------------------------------- copy

    [Fact]
    public async Task CopyMostradorWithRemarcacion40_YieldsBaseTimes153_LeavingMostradorUntouched_AndCarriesTheFloor()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/copy",
            new { name = "Mostrador 40", remarcacionPercentage = 40m, effectiveFrom = Dates[1] });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        var copy = body.GetProperty("priceList");
        var copyId = copy.GetProperty("id").GetGuid();
        Assert.NotEqual(w.Mostrador, copyId);
        Assert.Equal("Mostrador 40", copy.GetProperty("name").GetString());
        Assert.False(copy.GetProperty("isDefault").GetBoolean());
        Assert.Equal(w.Reparto, copy.GetProperty("floorPriceListId").GetGuid()); // floor carried over
        Assert.Equal(2, body.GetProperty("entriesCopied").GetInt32());

        var copied = await BreakdownAsync(w, copyId, Dates[1]);
        Assert.Equal(11_400m, Item(copied, w.Bola).GetProperty("base").GetDecimal());
        Assert.Equal(17_442m, Item(copied, w.Bola).GetProperty("final").GetDecimal()); // 11.400 x 1,53
        Assert.Contains(Item(copied, w.Bola).GetProperty("components").EnumerateArray(),
            c => c.GetProperty("code").GetString() == "REMARCACION" && c.GetProperty("percentage").GetDecimal() == 40m);

        var mostrador = await BreakdownAsync(w, w.Mostrador, Dates[1]);
        Assert.Equal(16_872m, Item(mostrador, w.Bola).GetProperty("final").GetDecimal()); // untouched

        // Independent lists: a new price on the copy never moves Mostrador.
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{copyId}/entries",
            new { presentationId = w.Bola, unitPrice = 12_000m, effectiveFrom = Dates[2] })).StatusCode);
        Assert.Equal(11_400m, Item(await BreakdownAsync(w, w.Mostrador, Dates[2]), w.Bola).GetProperty("base").GetDecimal());

        Assert.Equal(1L, Scalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'price-list.copied'", copyId));
    }

    [Fact]
    public async Task Copy_WithAnExplicitComponentSet_UsesItInsteadOfTheSources()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        object[] custom = [new { code = "IVA", label = "IVA (10,5%)", percentage = 10.5m, calculationBase = "Base", order = 1 }];

        var response = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/copy",
            new { name = "Solo IVA", components = custom, effectiveFrom = Dates[1] });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copyId = (await JsonAsync(response)).GetProperty("priceList").GetProperty("id").GetGuid();
        Assert.Equal(12_597m, Item(await BreakdownAsync(w, copyId, Dates[1]), w.Bola).GetProperty("final").GetDecimal()); // 11.400 x 1,105
    }

    [Fact]
    public async Task CopyBelowTheFloor_IsRefusedWithTheViolations_AndCreatesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var before = Scalar("SELECT count(*) FROM price_lists WHERE organization_id = $1", w.OrganizationId);

        var response = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/copy",
            new { name = "Mostrador 20", remarcacionPercentage = 20m, effectiveFrom = Dates[1] });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("price-below-floor", body.GetProperty("error").GetString());
        var violation = body.GetProperty("violations").EnumerateArray().Single();
        Assert.Equal(w.Bola, violation.GetProperty("presentationId").GetGuid());
        Assert.Equal("Bola de lomo", violation.GetProperty("productName").GetString());
        Assert.Equal(15_162m, violation.GetProperty("price").GetDecimal()); // 11.400 x 1,33
        Assert.Equal(16_530m, violation.GetProperty("floorPrice").GetDecimal());
        Assert.Equal(before, Scalar("SELECT count(*) FROM price_lists WHERE organization_id = $1", w.OrganizationId));
    }

    [Fact]
    public async Task Copy_RefusesABlankOrTakenName_AndAnUnknownSource()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/copy", new { name = " " })).StatusCode);
        var taken = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/copy", new { name = "mostrador" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("price-list-name-taken", (await JsonAsync(taken)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{Guid.NewGuid()}/copy", new { name = "X" })).StatusCode);
    }

    // ---------------------------------------------------------------- composition

    [Fact]
    public async Task PublishingAComposition_AddsADatedSet_KeepsTheHistory_AndIsAudited()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/composition",
            new { effectiveFrom = Dates[2], remarcacionPercentage = 40m });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var setId = (await JsonAsync(response)).GetProperty("id").GetGuid();
        Assert.Equal(17_442m, Item(await BreakdownAsync(w, w.Mostrador, Dates[2]), w.Bola).GetProperty("final").GetDecimal());
        Assert.Equal(16_872m, Item(await BreakdownAsync(w, w.Mostrador, Dates[1]), w.Bola).GetProperty("final").GetDecimal()); // before: unchanged
        Assert.Equal(2L, Scalar("SELECT count(*) FROM rate_component_sets WHERE price_list_id = $1", w.Mostrador));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'rate-component-set.published'", setId));

        var composition = await JsonAsync(await w.Admin.GetAsync($"/pricing/price-lists/{w.Mostrador}/composition?on={Dates[2]}"));
        Assert.Equal("list", composition.GetProperty("source").GetString());
        Assert.Equal(Dates[2], composition.GetProperty("effectiveFrom").GetString());
        Assert.Equal(2, composition.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task ACompositionThatLowersAPriceBelowTheFloor_IsRefusedWithViolations_AndNothingIsPublished()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/composition",
            new { effectiveFrom = Dates[2], components = Components(20m, flete: false) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("price-below-floor", body.GetProperty("error").GetString());
        var violation = body.GetProperty("violations").EnumerateArray().Single();
        Assert.Equal(w.Bola, violation.GetProperty("presentationId").GetGuid());
        Assert.Equal("Bola de lomo", violation.GetProperty("productName").GetString());
        Assert.Equal(15_162m, violation.GetProperty("price").GetDecimal());
        Assert.Equal(16_530m, violation.GetProperty("floorPrice").GetDecimal());
        Assert.Equal(1L, Scalar("SELECT count(*) FROM rate_component_sets WHERE price_list_id = $1", w.Mostrador));
    }

    [Fact]
    public async Task Composition_RefusesAnInvalidSet_ASecondSetOnTheSameDay_AndAMissingList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        object[] noBase = [new { code = "IVA", label = "IVA", percentage = 10.5m, calculationBase = "Nope", order = 1 }];
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/composition", new { effectiveFrom = Dates[2], components = noBase })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/composition", new { effectiveFrom = Dates[2] })).StatusCode);

        var sameDay = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/composition", new { effectiveFrom = Dates[0], components = Components(25m, flete: true) });
        Assert.Equal(HttpStatusCode.Conflict, sameDay.StatusCode);
        Assert.Equal("composition-already-exists-for-date", (await JsonAsync(sameDay)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{Guid.NewGuid()}/composition", new { effectiveFrom = Dates[2], components = Components(30m, flete: true) })).StatusCode);
    }

    // ---------------------------------------------------------------------- floor

    [Fact]
    public async Task PublishingAPriceBelowTheFloor_IsRefused_AndAPriceAboveItIsAccepted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var below = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/entries", new { presentationId = w.Bola, unitPrice = 10_000m, effectiveFrom = Dates[1] });
        Assert.Equal(HttpStatusCode.Conflict, below.StatusCode);
        var body = await JsonAsync(below);
        Assert.Equal("price-below-floor", body.GetProperty("error").GetString());
        Assert.Equal(14_800m, body.GetProperty("violations")[0].GetProperty("price").GetDecimal()); // 10.000 x 1,48
        Assert.Equal(16_530m, body.GetProperty("violations")[0].GetProperty("floorPrice").GetDecimal());

        Assert.Equal(HttpStatusCode.Created, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/entries", new { presentationId = w.Bola, unitPrice = 12_000m, effectiveFrom = Dates[1] })).StatusCode);
        // A counter-only product has no Reparto price to compare with: any price goes.
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/entries", new { presentationId = w.Lengua, unitPrice = 1m, effectiveFrom = Dates[1] })).StatusCode);
    }

    [Fact]
    public async Task RaisingTheFloorListAbovetTheListThatDependsOnIt_IsRefused_NamingTheDependentList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var raise = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Reparto}/entries", new { presentationId = w.Bola, unitPrice = 12_500m, effectiveFrom = Dates[1] });

        Assert.Equal(HttpStatusCode.Conflict, raise.StatusCode);
        var violation = (await JsonAsync(raise)).GetProperty("violations")[0];
        Assert.Equal(w.Mostrador, violation.GetProperty("priceListId").GetGuid());
        Assert.Equal("Mostrador", violation.GetProperty("priceListName").GetString());
        Assert.Equal(16_872m, violation.GetProperty("price").GetDecimal());
        Assert.Equal(18_125m, violation.GetProperty("floorPrice").GetDecimal()); // 12.500 x 1,45
    }

    [Fact]
    public async Task SettingAndClearingAFloor_IsValidatedAndAudited()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync(withFloor: false);

        var set = await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/floor", new { floorPriceListId = w.Reparto });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(w.Reparto, (await JsonAsync(set)).GetProperty("floorPriceListId").GetGuid());
        Assert.Equal(1L, Scalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'price-list.floor-changed'", w.Mostrador));

        // Itself, a list of nowhere, a cycle.
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/floor", new { floorPriceListId = w.Mostrador })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/floor", new { floorPriceListId = Guid.NewGuid() })).StatusCode);
        var cycle = await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{w.Reparto}/floor", new { floorPriceListId = w.Mostrador });
        Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode);
        Assert.Equal("floor-cycle", (await JsonAsync(cycle)).GetProperty("error").GetString());

        var cleared = await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/floor", new { floorPriceListId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(cleared)).GetProperty("floorPriceListId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{Guid.NewGuid()}/floor", new { floorPriceListId = (Guid?)null })).StatusCode);
    }

    [Fact]
    public async Task SettingAFloorThatTheListAlreadyViolates_IsRefusedWithTheViolations()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync(withFloor: false);
        // Reparto composed (16.530) is ABOVE Mostrador 20 %: pointing a cheaper list at Reparto must be refused.
        var cheap = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/copy", new { name = "Barata", remarcacionPercentage = 20m, effectiveFrom = Dates[1] });
        Assert.Equal(HttpStatusCode.Created, cheap.StatusCode);
        var cheapId = (await JsonAsync(cheap)).GetProperty("priceList").GetProperty("id").GetGuid();

        var response = await w.Admin.PutAsJsonAsync($"/pricing/price-lists/{cheapId}/floor", new { floorPriceListId = w.Reparto });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("price-below-floor", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM price_lists WHERE id = $1 AND floor_price_list_id IS NOT NULL", cheapId));
    }

    // ----------------------------------------------------------------- permissions

    [Fact]
    public async Task EveryNewRoute_RequiresManageCatalog_AndASignedInCaller()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var staff = await SignedInAsync(w.OrganizationId, w.BranchId, Permission.ViewSales);
        var id = w.Mostrador;

        Assert.Contains((await staff.GetAsync($"/pricing/price-lists/{id}/breakdown")).StatusCode, Denied);
        Assert.Contains((await staff.GetAsync($"/pricing/price-lists/{id}/composition")).StatusCode, Denied);
        Assert.Contains((await staff.PostAsJsonAsync($"/pricing/price-lists/{id}/copy", new { name = "X" })).StatusCode, Denied);
        Assert.Contains((await staff.PostAsJsonAsync($"/pricing/price-lists/{id}/composition", new { effectiveFrom = Dates[2], components = Components(40m, false) })).StatusCode, Denied);
        Assert.Contains((await staff.PutAsJsonAsync($"/pricing/price-lists/{id}/floor", new { floorPriceListId = (Guid?)null })).StatusCode, Denied);

        var noBranch = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Contains((await noBranch.GetAsync($"/pricing/price-lists/{id}/breakdown")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Found });
    }

    // ------------------------------------------------------------------- customers

    [Fact]
    public async Task CustomerEndpoints_AcceptAndReturnPriceListIdAndName_ClearWithAnEmptyGuid_AndRefuseAForeignList()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var other = await NewWorldAsync();

        object Body(Guid? priceListId) => new
        {
            customerKind = "Wholesale", displayName = "Carnicería Don Pepe", legalName = (string?)null,
            taxIdType = "None", taxId = (string?)null, taxCondition = "ConsumidorFinal",
            phone = (string?)null, email = (string?)null, addressStreet = (string?)null, addressNumber = (string?)null,
            neighborhood = (string?)null, locality = (string?)null, province = (string?)null, postalCode = (string?)null,
            deliveryNotes = (string?)null, discountPercentage = (decimal?)null, paymentTerms = (string?)null, notes = (string?)null,
            priceListId,
        };

        var created = await w.Admin.PostAsJsonAsync("/customers", Body(w.Reparto));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customerId = (await JsonAsync(created)).GetProperty("customerId").GetGuid();

        var read = await JsonAsync(await w.Admin.GetAsync($"/customers/{customerId}"));
        Assert.Equal(w.Reparto, read.GetProperty("priceListId").GetGuid());
        Assert.Equal("Reparto", read.GetProperty("priceListName").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.PostAsJsonAsync("/customers", Body(other.Reparto))).StatusCode);

        object Update(Guid? priceListId) => new
        {
            displayName = "Carnicería Don Pepe", legalName = (string?)null, taxIdType = "None", taxId = (string?)null, taxCondition = "ConsumidorFinal",
            phone = (string?)null, email = (string?)null, addressStreet = (string?)null, addressNumber = (string?)null, neighborhood = (string?)null,
            locality = (string?)null, province = (string?)null, postalCode = (string?)null, deliveryNotes = (string?)null,
            discountPercentage = (decimal?)null, paymentTerms = (string?)null, notes = (string?)null, isEnabled = true, priceListId,
        };

        var moved = await w.Admin.PutAsJsonAsync($"/customers/{customerId}", Update(w.Mostrador));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal("Mostrador", (await JsonAsync(moved)).GetProperty("priceListName").GetString());

        var kept = await w.Admin.PutAsJsonAsync($"/customers/{customerId}", Update(null)); // omitted/null keeps
        Assert.Equal(w.Mostrador, (await JsonAsync(kept)).GetProperty("priceListId").GetGuid());

        var cleared = await w.Admin.PutAsJsonAsync($"/customers/{customerId}", Update(Guid.Empty));
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(cleared)).GetProperty("priceListId").ValueKind);
    }
}
