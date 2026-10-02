using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Stock;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// `/stock` (T3): on hand per presentation of the selected branch (derived by SUM from the append-only movements),
/// movement history, manual adjustments, minimum levels and the low-stock list. Shares the fixture of the receptions tests
/// because the acceptance scenarios cross both (receive -> stock -> void).
/// </summary>
public sealed partial class PurchaseReceptionEndpointTests
{
    private static async Task<JsonElement> StockOfAsync(Scenario s, Guid presentation, string query = "") =>
        (await s.Admin.GetFromJsonAsync<JsonElement>($"/stock{query}")).EnumerateArray()
            .Single(e => e.GetProperty("presentationId").GetGuid() == presentation);

    private static Task<HttpResponseMessage> AdjustAsync(Scenario s, Guid presentation, string kind, decimal quantity, string? reason = "Conteo físico") =>
        s.Admin.PostAsJsonAsync("/stock/adjustments", new { presentationId = presentation, kind, quantity, reason });

    [Fact]
    public async Task AcceptanceScenario_StockFollowsTheReception_AndItsVoid_WithBothMovementsInTheHistory()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-scenario@example.com");

        var untouched = await StockOfAsync(s, s.Presentation);
        Assert.Equal(0m, untouched.GetProperty("onHand").GetDecimal()); // a presentation with no movement is listed with zero
        Assert.Equal(JsonValueKind.Null, untouched.GetProperty("lastMovementAtUtc").ValueKind);

        var id = (await CreateDraftAsync(s, [s.Line(120m, 4000m, lot: "L-77")])).GetProperty("id").GetGuid();
        Assert.Equal(0m, (await StockOfAsync(s, s.Presentation)).GetProperty("onHand").GetDecimal()); // a draft moves nothing
        await ConfirmAsync(s, id);

        var level = await StockOfAsync(s, s.Presentation);
        Assert.Equal(120m, level.GetProperty("onHand").GetDecimal());
        Assert.Equal("Media res", level.GetProperty("productName").GetString());
        Assert.Equal("Kg", level.GetProperty("presentationName").GetString());
        Assert.Equal("Weighted", level.GetProperty("quantityBehavior").GetString());
        Assert.NotEqual(JsonValueKind.Null, level.GetProperty("lastMovementAtUtc").ValueKind);
        Assert.False(level.GetProperty("belowMinimum").GetBoolean());

        await VoidAsync(s, id, "Devuelta");
        Assert.Equal(0m, (await StockOfAsync(s, s.Presentation)).GetProperty("onHand").GetDecimal());

        var history = await s.Admin.GetFromJsonAsync<JsonElement>($"/stock/{s.Presentation}/movements");
        Assert.Equal(2, history.GetProperty("total").GetInt32());
        Assert.Equal(0m, history.GetProperty("onHand").GetDecimal());
        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Reversal", "PurchaseReceipt"], items.Select(i => i.GetProperty("kind").GetString()!).ToArray()); // newest first
        Assert.Equal([-120m, 120m], items.Select(i => i.GetProperty("quantity").GetDecimal()).ToArray());
        Assert.Equal([0m, 120m], items.Select(i => i.GetProperty("balanceAfter").GetDecimal()).ToArray());
        Assert.All(items, i => Assert.Equal("R01-W-1", i.GetProperty("sourceNumber").GetString()));
        Assert.Equal("PurchaseReceptionVoid", items[0].GetProperty("sourceType").GetString());
        Assert.Equal("Devuelta", items[0].GetProperty("reason").GetString());
        Assert.Equal(items[1].GetProperty("id").GetGuid(), items[0].GetProperty("reversesMovementId").GetGuid());
        Assert.Equal("L-77", items[1].GetProperty("lotCode").GetString());
    }

    [Fact]
    public async Task ManualAdjustments_OpeningShrinkageCountCorrection_MoveTheOnHand_WithTheirReason()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-adjust@example.com");

        var opening = await AdjustAsync(s, s.Presentation, "Opening", 50m, "Stock inicial");
        Assert.Equal(HttpStatusCode.Created, opening.StatusCode);
        var body = await JsonOf(opening);
        Assert.Equal(50m, body.GetProperty("onHand").GetDecimal());
        Assert.Equal("Opening", body.GetProperty("movement").GetProperty("kind").GetString());
        Assert.Equal("Stock inicial", body.GetProperty("movement").GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(s, s.Presentation, "Shrinkage", -2.5m, "Merma")).StatusCode);
        Assert.Equal(47.5m, (await StockOfAsync(s, s.Presentation)).GetProperty("onHand").GetDecimal());
        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(s, s.Presentation, "CountCorrection", -7.5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(s, s.Presentation, "CountCorrection", 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(s, s.Presentation, "Adjustment", 0.25m)).StatusCode);
        Assert.Equal(41.25m, (await StockOfAsync(s, s.Presentation)).GetProperty("onHand").GetDecimal());

        using var owner = OpenOwner();
        Assert.Equal(5L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE presentation_id = $1 AND source_type IS NULL", s.Presentation));
        Assert.Equal(5L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE action = 'stock.adjusted' AND organization_id = $1", s.OrganizationId));
    }

    [Theory]
    [InlineData("Opening", -5, "Conteo", "quantity")]
    [InlineData("Shrinkage", 5, "Merma", "quantity")]
    [InlineData("Adjustment", 0, "Ajuste", "quantity")]
    [InlineData("Opening", 1.2345, "Conteo", "quantity")]
    [InlineData("PurchaseReceipt", 5, "Conteo", "kind")]
    [InlineData("Sale", -5, "Conteo", "kind")]
    [InlineData("Reversal", 5, "Conteo", "kind")]
    [InlineData("Bogus", 5, "Conteo", "kind")]
    [InlineData("Opening", 5, "   ", "reason")]
    [InlineData("Opening", 5, null, "reason")]
    public async Task ManualAdjustment_RefusesBadSignKindPrecisionOrMissingReason(string kind, double quantity, string? reason, string field)
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync($"stock-invalid-{Guid.NewGuid():N}@example.com");

        var response = await AdjustAsync(s, s.Presentation, kind, (decimal)quantity, reason);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonOf(response)).GetProperty("errors").TryGetProperty(field, out _), field);
        Assert.Equal(0m, OnHand(s.Presentation));
    }

    [Fact]
    public async Task ManualAdjustment_OfAFixedQuantityPresentation_MustBeWhole_AndAnUnknownPresentationIs404()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-fixed@example.com", behavior: "FixedQuantity");

        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(s, s.Presentation, "Opening", 1.5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AdjustAsync(s, s.Presentation, "Opening", 12m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdjustAsync(s, Guid.NewGuid(), "Opening", 1m)).StatusCode);

        Guid foreign;
        using (var owner = OpenOwner())
        {
            foreign = Presentation(s.OrganizationId, Branch(owner, s.OrganizationId, "Otra"));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await AdjustAsync(s, foreign, "Opening", 1m)).StatusCode); // another branch's presentation
    }

    [Fact]
    public async Task Minimums_FlagBelowMinimum_FeedTheLowStockList_AndClearWhenStockRecovers()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-minimum@example.com");
        var other = Presentation(s.OrganizationId, s.BranchId, "Chorizo", "Unidad", "FixedQuantity");

        await AdjustAsync(s, s.Presentation, "Opening", 8m);
        await AdjustAsync(s, other, "Opening", 100m);

        var set = await s.Admin.PutAsJsonAsync($"/stock/minimums/{s.Presentation}", new { minimumQuantity = 10m });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(10m, (await JsonOf(set)).GetProperty("minimumQuantity").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/stock/minimums/{other}", new { minimumQuantity = 20 })).StatusCode);

        var level = await StockOfAsync(s, s.Presentation);
        Assert.True(level.GetProperty("belowMinimum").GetBoolean());
        Assert.Equal(10m, level.GetProperty("minimumQuantity").GetDecimal());
        Assert.Equal(2m, level.GetProperty("shortfall").GetDecimal());
        Assert.False((await StockOfAsync(s, other)).GetProperty("belowMinimum").GetBoolean());

        var low = (await s.Admin.GetFromJsonAsync<JsonElement>("/stock/low")).EnumerateArray().ToList();
        Assert.Equal([s.Presentation], low.Select(e => e.GetProperty("presentationId").GetGuid()).ToArray());
        var onlyBelow = (await s.Admin.GetFromJsonAsync<JsonElement>("/stock?onlyBelowMinimum=true")).EnumerateArray().ToList();
        Assert.Equal([s.Presentation], onlyBelow.Select(e => e.GetProperty("presentationId").GetGuid()).ToArray());
        Assert.Equal(2, (await s.Admin.GetFromJsonAsync<JsonElement>("/stock")).GetArrayLength());

        // Receiving enough lifts it out of the list; updating the minimum is an update, not a second row.
        await AdjustAsync(s, s.Presentation, "CountCorrection", 5m);
        Assert.Empty((await s.Admin.GetFromJsonAsync<JsonElement>("/stock/low")).EnumerateArray());
        await s.Admin.PutAsJsonAsync($"/stock/minimums/{s.Presentation}", new { minimumQuantity = 20m });
        Assert.Single((await s.Admin.GetFromJsonAsync<JsonElement>("/stock/low")).EnumerateArray());

        // Clearing the minimum removes the alert.
        var cleared = await s.Admin.PutAsJsonAsync($"/stock/minimums/{s.Presentation}", new { minimumQuantity = (decimal?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Empty((await s.Admin.GetFromJsonAsync<JsonElement>("/stock/low")).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await StockOfAsync(s, s.Presentation)).GetProperty("minimumQuantity").ValueKind);
    }

    [Fact]
    public async Task NegativeStock_IsFlaggedAgainstAZeroMinimum()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-negative@example.com");
        await AdjustAsync(s, s.Presentation, "CountCorrection", -3m);
        await s.Admin.PutAsJsonAsync($"/stock/minimums/{s.Presentation}", new { minimumQuantity = 0m });

        var level = await StockOfAsync(s, s.Presentation);
        Assert.Equal(-3m, level.GetProperty("onHand").GetDecimal());
        Assert.True(level.GetProperty("belowMinimum").GetBoolean());
    }

    [Theory]
    [InlineData(-1, "Weighted")]
    [InlineData(1.2345, "Weighted")]
    [InlineData(2.5, "FixedQuantity")]
    public async Task Minimum_RefusesNegativeOrImpreciseQuantities_AndAnUnknownPresentationIs404(double minimum, string behavior)
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync($"stock-min-invalid-{Guid.NewGuid():N}@example.com", behavior: behavior);

        var refused = await s.Admin.PutAsJsonAsync($"/stock/minimums/{s.Presentation}", new { minimumQuantity = (decimal)minimum });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PutAsJsonAsync($"/stock/minimums/{Guid.NewGuid()}", new { minimumQuantity = 1m })).StatusCode);
    }

    [Fact]
    public async Task StockList_FiltersBySearch_AndHistoryIsPaged_NewestFirst()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-paging@example.com");
        var chorizo = Presentation(s.OrganizationId, s.BranchId, "Chorizo parrillero", "Unidad", "FixedQuantity");

        Assert.Equal([chorizo], (await s.Admin.GetFromJsonAsync<JsonElement>("/stock?search=chori")).EnumerateArray().Select(e => e.GetProperty("presentationId").GetGuid()).ToArray());
        Assert.Equal([s.Presentation], (await s.Admin.GetFromJsonAsync<JsonElement>("/stock?search=MEDIA")).EnumerateArray().Select(e => e.GetProperty("presentationId").GetGuid()).ToArray());

        for (var i = 1; i <= 5; i++)
        {
            await AdjustAsync(s, s.Presentation, "Opening", i, $"carga {i}");
        }

        var first = await s.Admin.GetFromJsonAsync<JsonElement>($"/stock/{s.Presentation}/movements?page=1&pageSize=2");
        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal(15m, first.GetProperty("onHand").GetDecimal());
        Assert.Equal(["carga 5", "carga 4"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()!).ToArray());
        var last = await s.Admin.GetFromJsonAsync<JsonElement>($"/stock/{s.Presentation}/movements?page=3&pageSize=2");
        Assert.Equal(["carga 1"], last.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()!).ToArray());
        Assert.Equal(1m, last.GetProperty("items")[0].GetProperty("balanceAfter").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync($"/stock/{s.Presentation}/movements?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"/stock/{Guid.NewGuid()}/movements")).StatusCode);
    }

    [Fact]
    public async Task Stock_RequiresASelectedBranch_AndThePermission()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-access@example.com");

        var noBranch = await SignInAsync("stock-access@example.com");
        foreach (var route in new[] { "/stock", "/stock/low", $"/stock/{s.Presentation}/movements" })
        {
            var response = await noBranch.GetAsync(route);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("branch-selection-required", await ErrorOf(response));
        }

        var cashier = await SignInAsync(await CreateCashierAsync(s.OrganizationId, s.BranchId), s.BranchId);
        foreach (var route in new[] { "/stock", "/stock/low" })
        {
            Assert.True((await cashier.GetAsync(route)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found or HttpStatusCode.Unauthorized);
        }

        Assert.True((await cashier.PostAsJsonAsync("/stock/adjustments", new { presentationId = s.Presentation, kind = "Opening", quantity = 1, reason = "x" })).StatusCode
            is HttpStatusCode.Forbidden or HttpStatusCode.Found or HttpStatusCode.Unauthorized);
        Assert.Equal(0m, OnHand(s.Presentation));
    }

    [Fact]
    public async Task StockMovementWriter_IgnoringDuplicates_AppliesASourceLineExactlyOnce()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("stock-idempotent@example.com");
        var saleId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await TenantScopeSql.ApplyAsync(connection, tx, new Commerce.Cloud.Api.Tenancy.CloudTenantScope(s.OrganizationId, BranchId: s.BranchId), CancellationToken.None);

        NewStockMovement Sale() => new(
            Guid.NewGuid(), s.OrganizationId, s.BranchId, s.Presentation, -2.5m, StockMovementKind.Sale, "PosSale", saleId, lineId);

        Assert.True(await StockMovementWriter.InsertAsync(connection, tx, Sale(), CancellationToken.None, ignoreDuplicateSourceLine: true));
        Assert.False(await StockMovementWriter.InsertAsync(connection, tx, Sale(), CancellationToken.None, ignoreDuplicateSourceLine: true));
        await tx.CommitAsync();

        Assert.Equal(-2.5m, OnHand(s.Presentation));
    }
}
