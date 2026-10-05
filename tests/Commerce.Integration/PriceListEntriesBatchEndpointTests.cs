using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Application.Time;
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
/// price-editing-and-desktop-polish T4: `POST /pricing/price-lists/{id}/entries/batch` publishes many base prices of one
/// list at once, all-or-nothing, under the same permission, branch and floor rules as `POST .../entries`. A batch price for
/// a presentation that already has an entry effective that same day replaces it as a correction (0043); the single
/// endpoint keeps refusing that case. The clock is 01:30 UTC on 2026-10-03, i.e. 22:30 of 2026-10-02 in Argentina: the
/// default effective date must be the business day (10-02), never the UTC date. Vaca Verde figures: Bola de lomo base
/// 11.400 on Reparto (x 1,45 = 16.530, the floor) and Mostrador (x 1,48 = 16.872).
/// </summary>
[Collection("Postgres")]
public sealed class PriceListEntriesBatchEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "test-password";
    private const string MigrationFile = "0043_price_entry_same_day_correction.sql";
    private const string Initial = "2026-10-01";
    private const string BusinessDay = "2026-10-02";
    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.Forbidden, HttpStatusCode.Found];
    private static readonly IBusinessClock Clock = new BusinessClock(new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 1, 30, 0, TimeSpan.Zero)));

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PriceListEntriesBatchEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString)
                .ConfigureServices(services => services.AddSingleton(Clock)));
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
        HttpClient Admin, Guid OrganizationId, Guid BranchId, Guid Bola, Guid Lengua, Guid Reparto, Guid Mostrador, Guid Bare);

    private static void Exec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd.ExecuteScalar();
    }

    private static long EntryCount(Guid list) => Convert.ToInt64(Scalar("SELECT count(*) FROM price_list_entries WHERE price_list_id = $1", list));

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

    private static async Task<Guid> CreateListAsync(HttpClient admin, string name, bool isDefault)
    {
        var created = await admin.PostAsJsonAsync("/pricing/price-lists", new { name, isDefault });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Mostrador (default, floor Reparto) and Reparto price Bola de lomo; Mostrador also Lengua; "Sin tasas" is bare.</summary>
    private async Task<World> NewWorldAsync()
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

        var mostrador = await CreateListAsync(admin, "Mostrador", true);
        var reparto = await CreateListAsync(admin, "Reparto", false);
        var bare = await CreateListAsync(admin, "Sin tasas", false);

        async Task Price(Guid list, Guid presentation, decimal price) =>
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/pricing/price-lists/{list}/entries", new { presentationId = presentation, unitPrice = price, effectiveFrom = Initial })).StatusCode);
        async Task Composition(Guid list, object[] components) =>
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/pricing/price-lists/{list}/composition", new { effectiveFrom = Initial, components })).StatusCode);

        await Price(reparto, bola, 11_400m);
        await Composition(reparto, Components(25m, flete: true));
        await Price(mostrador, bola, 11_400m);
        await Price(mostrador, lengua, 7_817.57m);
        await Composition(mostrador, Components(35m, flete: false));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/pricing/price-lists/{mostrador}/floor", new { floorPriceListId = reparto })).StatusCode);

        return new World(admin, organizationId, branchId, bola, lengua, reparto, mostrador, bare);
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid list, string? effectiveFrom, params object[] entries) =>
        client.PostAsJsonAsync($"/pricing/price-lists/{list}/entries/batch", new { effectiveFrom, entries });

    private static object Entry(Guid presentationId, decimal unitPrice) => new { presentationId, unitPrice };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return [.. (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    private async Task<JsonElement> HistoryAsync(World w, Guid list, Guid presentation)
    {
        var response = await w.Admin.GetAsync($"/pricing/price-lists/{list}/presentations/{presentation}/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task TwoPrices_ArePublished_EffectiveOnTheBusinessDay_WhenNoDateIsSent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, null, Entry(w.Bola, 12_540.50m), Entry(w.Lengua, 8_000m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(2, body.GetProperty("published").GetInt32());
        var entries = body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal([w.Bola, w.Lengua], entries.Select(e => e.GetProperty("presentationId").GetGuid()));
        Assert.Equal([12_540.50m, 8_000m], entries.Select(e => e.GetProperty("unitPrice").GetDecimal()));
        Assert.All(entries, e =>
        {
            Assert.Equal(BusinessDay, e.GetProperty("effectiveFrom").GetString()); // not the UTC date (10-03)
            Assert.Equal(w.Bare, e.GetProperty("priceListId").GetGuid());
            Assert.Equal(w.OrganizationId, e.GetProperty("organizationId").GetGuid());
            Assert.Equal(w.BranchId, e.GetProperty("branchId").GetGuid());
            Assert.Equal("Manual", e.GetProperty("source").GetString());
        });
        Assert.Equal(2L, EntryCount(w.Bare));
        Assert.Equal(12_540.50m, (await HistoryAsync(w, w.Bare, w.Bola))[0].GetProperty("unitPrice").GetDecimal());

        // One audit record for the whole batch: the list, the date and how many prices.
        Assert.Equal(1L, Convert.ToInt64(Scalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'price-list.entries-published'", w.Bare)));
        Assert.Equal("2", Scalar("SELECT new_value->>'count' FROM audit_log WHERE entity_id = $1 AND action = 'price-list.entries-published'", w.Bare));
        Assert.Equal(BusinessDay, Scalar("SELECT new_value->>'effectiveFrom' FROM audit_log WHERE entity_id = $1 AND action = 'price-list.entries-published'", w.Bare));
        var entryIds = entries.Select(e => e.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT count(*) FROM audit_log WHERE entity_id = ANY($1)", entryIds)));
    }

    [Fact]
    public async Task AnExplicitDate_IsUsedAsSent()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, "2026-11-01", Entry(w.Bola, 100m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2026-11-01", (await JsonAsync(response)).GetProperty("entries")[0].GetProperty("effectiveFrom").GetString());
    }

    // A batch may replace a same-day price, so it must never reach a day already in the past: that would rewrite the
    // history a sale or an order was priced with. Today and future days stay correctable.
    [Fact]
    public async Task APastEffectiveDate_IsRefused_SoTheHistoryIsNeverRewritten()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, Initial, Entry(w.Bola, 100m));

        Assert.Equal(["effectiveFrom"], await ErrorKeysAsync(response));
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public async Task InvalidEntries_AreReportedByTheirIndexInTheBatch_AndNothingIsWritten()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, null,
            Entry(w.Bola, 100m), Entry(w.Lengua, 0m), Entry(Guid.NewGuid(), 10.123m), new { presentationId = (Guid?)null, unitPrice = -5m }, new { presentationId = Guid.NewGuid() });

        Assert.Equal(
            ["entries[1].unitPrice", "entries[2].unitPrice", "entries[3].presentationId", "entries[3].unitPrice", "entries[4].unitPrice"],
            await ErrorKeysAsync(response));
        Assert.Equal(0L, EntryCount(w.Bare));
        Assert.Equal(0L, Convert.ToInt64(Scalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'price-list.entries-published'", w.Bare)));
    }

    [Fact]
    public async Task AnEmptyOrOversizedBatch_IsRefusedOnEntries()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        Assert.Equal(["entries"], await ErrorKeysAsync(await PublishAsync(w.Admin, w.Bare, null)));
        Assert.Equal(["entries"], await ErrorKeysAsync(await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Bare}/entries/batch", new { effectiveFrom = (string?)null })));
        var tooMany = Enumerable.Range(0, 2001).Select(_ => Entry(Guid.NewGuid(), 1m)).ToArray();
        Assert.Equal(["entries"], await ErrorKeysAsync(await PublishAsync(w.Admin, w.Bare, null, tooMany)));
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    [Fact]
    public async Task APresentationSentTwice_IsRefusedOnItsSecondOccurrence()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, null, Entry(w.Bola, 100m), Entry(w.Lengua, 200m), Entry(w.Bola, 110m));

        Assert.Equal(["entries[2].presentationId"], await ErrorKeysAsync(response));
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    [Fact]
    public async Task APresentationOutsideTheBranchCatalog_IsRefused_AndNothingIsWritten()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var other = await NewWorldAsync();

        var response = await PublishAsync(w.Admin, w.Bare, null, Entry(w.Bola, 100m), Entry(Guid.NewGuid(), 200m), Entry(other.Lengua, 300m));

        Assert.Equal(["entries[1].presentationId", "entries[2].presentationId"], await ErrorKeysAsync(response));
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    // ---------------------------------------------------------------- floor rule

    [Fact]
    public async Task APriceBelowTheFloor_RefusesTheWholeBatch_WithTheViolations()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        // Bola 10.000 x 1,48 = 14.800 < Reparto's 16.530; Lengua is fine (Reparto does not price it).
        var response = await PublishAsync(w.Admin, w.Mostrador, null, Entry(w.Lengua, 8_000m), Entry(w.Bola, 10_000m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("price-below-floor", body.GetProperty("error").GetString());
        var violation = Assert.Single(body.GetProperty("violations").EnumerateArray());
        Assert.Equal(w.Mostrador, violation.GetProperty("priceListId").GetGuid());
        Assert.Equal(w.Reparto, violation.GetProperty("floorPriceListId").GetGuid());
        Assert.Equal(w.Bola, violation.GetProperty("presentationId").GetGuid());
        Assert.Equal(14_800m, violation.GetProperty("price").GetDecimal());
        Assert.Equal(16_530m, violation.GetProperty("floorPrice").GetDecimal());
        Assert.Equal(2L, EntryCount(w.Mostrador)); // the two seed entries; Lengua was not written either
    }

    [Fact]
    public async Task RaisingAFloorList_AboveTheListsThatDependOnIt_IsRefusedToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();

        // Reparto 12.000 x 1,45 = 17.400 > Mostrador's 16.872: Mostrador would fall under its floor.
        var response = await PublishAsync(w.Admin, w.Reparto, null, Entry(w.Bola, 12_000m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var violation = Assert.Single((await JsonAsync(response)).GetProperty("violations").EnumerateArray());
        Assert.Equal(w.Mostrador, violation.GetProperty("priceListId").GetGuid());
        Assert.Equal(1L, EntryCount(w.Reparto));
    }

    // ---------------------------------------------------------------- same-day correction

    [Fact]
    public async Task ASameDayPrice_IsReplacedAsACorrection_KeepingTheOldPriceInTheAudit()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var first = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/entries", new { presentationId = w.Bola, unitPrice = 12_000m, effectiveFrom = BusinessDay });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await JsonAsync(first)).GetProperty("id").GetGuid();
        var publishedAt = new DateTimeOffset((DateTime)Scalar("SELECT created_at_utc FROM price_list_entries WHERE id = $1", firstId)!);

        var response = await PublishAsync(w.Admin, w.Mostrador, null, Entry(w.Bola, 12_500m), Entry(w.Lengua, 8_000m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replaced = (await JsonAsync(response)).GetProperty("entries")[0];
        Assert.Equal(firstId, replaced.GetProperty("id").GetGuid());
        Assert.Equal(12_500m, replaced.GetProperty("unitPrice").GetDecimal());
        var history = (await HistoryAsync(w, w.Mostrador, w.Bola)).EnumerateArray().ToList();
        Assert.Equal([BusinessDay, Initial], history.Select(e => e.GetProperty("effectiveFrom").GetString()));
        Assert.Equal([12_500m, 11_400m], history.Select(e => e.GetProperty("unitPrice").GetDecimal()));

        // The audit keeps the corrected price; the stored row is the new one.
        const string Audit = "FROM audit_log WHERE entity_id = $1 AND action = 'price-list.entries-published'";
        Assert.Equal("12000.00", Scalar($"SELECT new_value->'replaced'->0->>'oldUnitPrice' {Audit}", w.Mostrador));
        Assert.Equal("12500.00", Scalar($"SELECT new_value->'replaced'->0->>'newUnitPrice' {Audit}", w.Mostrador));
        Assert.Equal(w.Bola.ToString(), Scalar($"SELECT new_value->'replaced'->0->>'presentationId' {Audit}", w.Mostrador));
        Assert.Equal(1L, Convert.ToInt64(Scalar($"SELECT jsonb_array_length(new_value->'replaced') {Audit}", w.Mostrador)));

        // The replicas see the correction: the catalog sync cursor (created_at_utc) moved and the snapshot prices it.
        using (var s = _factory.Services.CreateScope())
        {
            var store = s.ServiceProvider.GetRequiredService<PostgresPriceListStore>();
            var scope = new CloudTenantScope(w.OrganizationId, BranchId: w.BranchId);
            var changed = await store.ListEffectiveChangedSinceAsync(scope, w.Mostrador, publishedAt, DateOnly.Parse(BusinessDay), CancellationToken.None);
            Assert.Equal(12_500m, Assert.Single(changed, e => e.PresentationId == w.Bola).UnitPrice);
            var snapshot = await store.ListItemsAsOfAsync(scope, w.Mostrador, DateOnly.Parse(BusinessDay), CancellationToken.None);
            Assert.Equal(12_500m, snapshot.Single(i => i.PresentationId == w.Bola).UnitPrice);
        }

        // The single endpoint still refuses a second price for the same day.
        var again = await w.Admin.PostAsJsonAsync($"/pricing/price-lists/{w.Mostrador}/entries", new { presentationId = w.Bola, unitPrice = 13_000m, effectiveFrom = BusinessDay });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("entry-already-exists-for-date", (await JsonAsync(again)).GetProperty("error").GetString());
        Assert.Equal(12_500m, (await HistoryAsync(w, w.Mostrador, w.Bola))[0].GetProperty("unitPrice").GetDecimal());
    }

    // ---------------------------------------------------------------- authorization and scope

    [Fact]
    public async Task ACallerWithoutManageCatalog_IsRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var staff = await SignedInAsync(w.OrganizationId, w.BranchId, Permission.ManageUsers);

        var response = await PublishAsync(staff, w.Bare, null, Entry(w.Bola, 100m));

        Assert.Contains(response.StatusCode, Denied);
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    [Fact]
    public async Task WithoutASelectedBranch_TheBatchIsRefused()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        w.Admin.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);

        var response = await PublishAsync(w.Admin, w.Bare, null, Entry(w.Bola, 100m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("branch-selection-required", (await JsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    [Fact]
    public async Task AnotherOrganizationsList_IsNotFound_LikeANonexistentOne()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var other = await NewWorldAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(other.Admin, w.Bare, null, Entry(other.Bola, 100m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(other.Admin, Guid.NewGuid(), null, Entry(other.Bola, 100m))).StatusCode);
        Assert.Equal(0L, EntryCount(w.Bare));
    }

    // ---------------------------------------------------------------- 0043: the narrow UPDATE exception

    [Fact]
    public async Task TheRuntimeRole_MayCorrectOnlyThePriceOfItsOwnTenantsEntries()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var w = await NewWorldAsync();
        var other = await NewWorldAsync();
        var entryId = (Guid)Scalar("SELECT id FROM price_list_entries WHERE price_list_id = $1 AND presentation_id = $2", w.Mostrador, w.Bola)!;

        int UpdateAs(World tenant, string sql)
        {
            using var runtime = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
            runtime.Open();
            using var tx = runtime.BeginTransaction();
            using (var org = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true), set_config('app.current_branch_id', $2, true)", runtime, tx))
            {
                org.Parameters.AddWithValue(tenant.OrganizationId.ToString());
                org.Parameters.AddWithValue(tenant.BranchId.ToString());
                org.ExecuteNonQuery();
            }

            using var cmd = new NpgsqlCommand(sql, runtime, tx);
            cmd.Parameters.AddWithValue(entryId);
            var rows = cmd.ExecuteNonQuery();
            tx.Commit();
            return rows;
        }

        // Another organization's row is invisible to the UPDATE (RLS), so nothing changes.
        Assert.Equal(0, UpdateAs(other, "UPDATE price_list_entries SET unit_price = 1 WHERE id = $1"));
        Assert.Equal(11_400m, (decimal)Scalar("SELECT unit_price FROM price_list_entries WHERE id = $1", entryId)!);

        // The owning tenant can correct the price; nothing else about an entry is updatable, and nothing is deletable.
        Assert.Equal(1, UpdateAs(w, "UPDATE price_list_entries SET unit_price = 11400 WHERE id = $1"));
        foreach (var sql in new[]
        {
            "UPDATE price_list_entries SET effective_from = effective_from + 1 WHERE id = $1",
            "UPDATE price_list_entries SET presentation_id = presentation_id WHERE id = $1",
            "UPDATE price_list_entries SET price_list_id = price_list_id WHERE id = $1",
            "UPDATE price_list_entries SET organization_id = organization_id WHERE id = $1",
            "UPDATE price_list_entries SET source = 'Manual' WHERE id = $1",
            "DELETE FROM price_list_entries WHERE id = $1",
        })
        {
            var ex = Assert.Throws<PostgresException>(() => UpdateAs(w, sql));
            Assert.Equal("42501", ex.SqlState);
        }
    }

    [Fact]
    public void TheMigration_IsMirroredVerbatimInTheDevInitSnapshot()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", MigrationFile))), init);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
