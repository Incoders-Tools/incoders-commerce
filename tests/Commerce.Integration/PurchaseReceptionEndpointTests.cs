using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// `/purchases/receptions`: draft create/edit, confirm (stock + supplier Invoice + cost history in ONE transaction),
/// void (compensating movements and Reversal), the duplicate-document guard and the per-branch R number.
/// </summary>
[Collection("Postgres")]
public sealed partial class PurchaseReceptionEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private const string Route = "/purchases/receptions";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PurchaseReceptionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndReset();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrationsAndReset()
    {
        using var owner = OpenOwner();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }

        Exec(owner,
            "TRUNCATE TABLE audit_log, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE");
    }

    /// <summary>A signed-in admin of a fresh organization with the branch selected, plus a supplier and one weighted presentation.</summary>
    private sealed record Scenario(HttpClient Admin, Guid OrganizationId, Guid BranchId, Guid SupplierId, Guid Presentation)
    {
        public object Line(decimal quantity = 120m, decimal unitCost = 4000m, string? lot = null, Guid? presentation = null) =>
            new { presentationId = presentation ?? Presentation, quantity, unitCost, lotCode = lot };
    }

    private async Task<Scenario> NewScenarioAsync(string email, int? paymentTermsDays = 30, string behavior = "Weighted")
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var bootstrap = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "Ruta 51", email, Password));
        bootstrap.EnsureSuccessStatusCode();
        var branchId = (await bootstrap.Content.ReadFromJsonAsync<BootstrapResponse>())!.BranchId;

        var admin = await SignInAsync(email, branchId);
        var supplier = await admin.PostAsJsonAsync("/suppliers", new
        {
            displayName = "Frigorífico Sur", taxIdType = "None", taxCondition = "ResponsableInscripto", paymentTermsDays,
        });
        supplier.EnsureSuccessStatusCode();
        var supplierId = (await supplier.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("supplierId").GetGuid();
        return new Scenario(admin, organizationId, branchId, supplierId, Presentation(organizationId, branchId, "Media res", "Kg", behavior));
    }

    private async Task<HttpClient> SignInAsync(string email, Guid? branchId = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        if (branchId is not null) client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branchId.ToString());
        return client;
    }

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).ToString("yyyy-MM-dd");

    private static string DaysFromToday(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).AddDays(days).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> ErrorOf(HttpResponseMessage response) => (await JsonOf(response)).GetProperty("error").GetString();

    private static async Task<JsonElement> CreateDraftAsync(
        Scenario s, object[]? lines = null, string? documentReference = null, string documentType = "Invoice",
        string? occurredOn = null, string? dueOn = null, string? notes = null)
    {
        var response = await s.Admin.PostAsJsonAsync(Route, new
        {
            supplierId = s.SupplierId, documentType, documentReference, occurredOn = occurredOn ?? Today(), dueOn, notes,
            lines = lines ?? [s.Line()],
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Scenario s, Guid id) =>
        s.Admin.PostAsJsonAsync($"{Route}/{id}/confirm", new { });

    private static Task<HttpResponseMessage> VoidAsync(Scenario s, Guid id, string? reason = "Cargado por error") =>
        s.Admin.PostAsJsonAsync($"{Route}/{id}/void", new { reason });

    private static decimal OnHand(Guid presentation)
    {
        using var owner = OpenOwner();
        return Scalar<decimal>(owner, "SELECT COALESCE(SUM(quantity), 0) FROM stock_movements WHERE presentation_id = $1", presentation);
    }

    private static async Task<decimal> BalanceAsync(Scenario s) =>
        (await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/summary")).GetProperty("balance").GetDecimal();

    [Fact]
    public async Task AcceptanceScenario_ConfirmingMediaRes_RaisesStock_InvoicesTheSupplier_RecordsTheCost_AndVoidingBringsItBack()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-scenario@example.com", paymentTermsDays: 30);

        var draft = await CreateDraftAsync(s, [s.Line(120m, 4000m, lot: "L-77")]);
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);
        Assert.Equal(480_000m, draft.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(0m, OnHand(s.Presentation)); // a draft moves nothing
        Assert.Equal(0m, await BalanceAsync(s));

        var confirmed = await ConfirmAsync(s, id);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var body = await JsonOf(confirmed);
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal("R01-W-1", body.GetProperty("number").GetString());
        Assert.Equal(DaysFromToday(30), body.GetProperty("dueOn").GetString());
        Assert.Equal(120m, OnHand(s.Presentation));

        // The supplier account: an Invoice of 480.000 due by the supplier's terms, referenced by the R number.
        Assert.Equal(480_000m, await BalanceAsync(s));
        var statement = await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/statement");
        var invoice = statement.GetProperty("movements").EnumerateArray().Single();
        Assert.Equal("Invoice", invoice.GetProperty("kind").GetString());
        Assert.Equal(480_000m, invoice.GetProperty("amount").GetDecimal());
        Assert.Equal(DaysFromToday(30), invoice.GetProperty("dueOn").GetString());
        Assert.Equal("R01-W-1", invoice.GetProperty("documentReference").GetString());
        Assert.Equal(invoice.GetProperty("id").GetGuid(), body.GetProperty("ledgerInvoiceMovementId").GetGuid());

        // The cost history and the movement carry the line.
        using (var owner = OpenOwner())
        {
            Assert.Equal(4000m, Scalar<decimal>(owner, "SELECT unit_cost FROM presentation_costs WHERE presentation_id = $1", s.Presentation));
            Assert.Equal("PurchaseReceipt", Scalar<string>(owner, "SELECT kind FROM stock_movements WHERE presentation_id = $1", s.Presentation));
            Assert.Equal("L-77", Scalar<string>(owner, "SELECT lot_code FROM stock_movements WHERE presentation_id = $1", s.Presentation));
        }

        var voided = await VoidAsync(s, id, "Mercadería devuelta");
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var voidBody = await JsonOf(voided);
        Assert.Equal("Voided", voidBody.GetProperty("status").GetString());
        Assert.Equal("Mercadería devuelta", voidBody.GetProperty("voidReason").GetString());
        Assert.Equal(0m, OnHand(s.Presentation));
        Assert.Equal(0m, await BalanceAsync(s));

        // Both history lines stay visible.
        var after = await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/statement");
        Assert.Equal(["Invoice", "Reversal"], after.GetProperty("movements").EnumerateArray().Select(m => m.GetProperty("kind").GetString()!).ToArray());
        // The ledger concepts are user-facing text in a Spanish UI.
        Assert.Equal(
            ["Recepción de mercadería R01-W-1", "Anulación de recepción R01-W-1: Mercadería devuelta"],
            after.GetProperty("movements").EnumerateArray().Select(m => m.GetProperty("concept").GetString()!).ToArray());
        using (var owner = OpenOwner())
        {
            Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM stock_movements WHERE presentation_id = $1", s.Presentation));
            Assert.Equal("Reversal", Scalar<string>(owner, "SELECT kind FROM stock_movements WHERE presentation_id = $1 AND quantity < 0", s.Presentation));
        }
    }

    [Fact]
    public async Task Confirm_NumbersReceptionsPerBranchInSequence_AndKeepsTheNumberAfterAVoid()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-numbers@example.com");

        var first = (await CreateDraftAsync(s, documentReference: "A-1")).GetProperty("id").GetGuid();
        var second = (await CreateDraftAsync(s, documentReference: "A-2")).GetProperty("id").GetGuid();

        Assert.Equal("R01-W-1", (await JsonOf(await ConfirmAsync(s, first))).GetProperty("number").GetString());
        Assert.Equal("R01-W-2", (await JsonOf(await ConfirmAsync(s, second))).GetProperty("number").GetString());
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(s, second)).StatusCode);

        var third = (await CreateDraftAsync(s, documentReference: "A-3")).GetProperty("id").GetGuid();
        Assert.Equal("R01-W-3", (await JsonOf(await ConfirmAsync(s, third))).GetProperty("number").GetString());
        Assert.Equal("R01-W-2", (await JsonOf(await s.Admin.GetAsync($"{Route}/{second}"))).GetProperty("number").GetString());
    }

    [Fact]
    public async Task Confirm_UsesTheSupplierDocumentReference_ForTheInvoice_AndAnExplicitDueDate()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-docref@example.com", paymentTermsDays: 30);

        var id = (await CreateDraftAsync(s, documentReference: "A-0001-00000123", dueOn: DaysFromToday(10))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(s, id)).StatusCode);

        var invoice = (await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/statement"))
            .GetProperty("movements").EnumerateArray().Single();
        Assert.Equal("A-0001-00000123", invoice.GetProperty("documentReference").GetString());
        Assert.Equal(DaysFromToday(10), invoice.GetProperty("dueOn").GetString());
    }

    [Fact]
    public async Task SameSupplierDocument_CannotBeConfirmedTwice_UntilTheFirstIsVoided()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-duplicate@example.com");

        var first = (await CreateDraftAsync(s, documentReference: "A-0001-00000123")).GetProperty("id").GetGuid();
        var second = (await CreateDraftAsync(s, documentReference: "  a-0001-00000123 ")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(s, first)).StatusCode);

        var refused = await ConfirmAsync(s, second);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("reception-duplicate-document", await ErrorOf(refused));

        // Nothing was written by the refused confirmation: still a draft, stock and account untouched.
        Assert.Equal("Draft", (await JsonOf(await s.Admin.GetAsync($"{Route}/{second}"))).GetProperty("status").GetString());
        Assert.Equal(120m, OnHand(s.Presentation));
        Assert.Equal(480_000m, await BalanceAsync(s));

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(s, first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(s, second)).StatusCode);
    }

    [Fact]
    public async Task Confirm_WithoutLines_Is400_AndConfirmingTwice_Is409()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-confirm-rules@example.com");

        var empty = (await CreateDraftAsync(s, lines: [])).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmAsync(s, empty)).StatusCode);

        var id = (await CreateDraftAsync(s)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(s, id)).StatusCode);
        var again = await ConfirmAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("reception-not-draft", await ErrorOf(again));
        Assert.Equal(120m, OnHand(s.Presentation));
        Assert.Equal(HttpStatusCode.NotFound, (await ConfirmAsync(s, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Void_NeedsAReason_AFreshConfirmedReception_AndOnlyHappensOnce()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-void-rules@example.com");
        var id = (await CreateDraftAsync(s)).GetProperty("id").GetGuid();

        var draftVoid = await VoidAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, draftVoid.StatusCode);
        Assert.Equal("reception-not-confirmed", await ErrorOf(draftVoid));

        await ConfirmAsync(s, id);
        Assert.Equal(HttpStatusCode.BadRequest, (await VoidAsync(s, id, reason: "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await VoidAsync(s, id, reason: null)).StatusCode);
        Assert.Equal(120m, OnHand(s.Presentation));

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(s, id)).StatusCode);
        var twice = await VoidAsync(s, id);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("reception-not-confirmed", await ErrorOf(twice));
        Assert.Equal(0m, OnHand(s.Presentation));
    }

    [Fact]
    public async Task ZeroCostReception_MovesStockButPostsNoInvoice()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-zero@example.com");
        var id = (await CreateDraftAsync(s, [s.Line(5m, 0m)])).GetProperty("id").GetGuid();

        var confirmed = await JsonOf(await ConfirmAsync(s, id));
        Assert.Equal(JsonValueKind.Null, confirmed.GetProperty("ledgerInvoiceMovementId").ValueKind);
        Assert.Equal(5m, OnHand(s.Presentation));
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/statement")).GetProperty("movements").GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(s, id)).StatusCode);
        Assert.Equal(0m, OnHand(s.Presentation));
    }

    [Fact]
    public async Task Update_ReplacesTheLines_OnlyWhileDraft_AndDetectsAConcurrentEdit()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-update@example.com");
        var other = Presentation(s.OrganizationId, s.BranchId, "Chorizo", "Unidad", "FixedQuantity");

        var draft = await CreateDraftAsync(s);
        var id = draft.GetProperty("id").GetGuid();
        var token = draft.GetProperty("updatedAtUtc").GetDateTimeOffset();

        var update = await s.Admin.PutAsJsonAsync($"{Route}/{id}", new
        {
            supplierId = s.SupplierId, documentType = "DeliveryNote", documentReference = "R-9", occurredOn = Today(), notes = "editado",
            expectedUpdatedAtUtc = token,
            lines = new[] { s.Line(10m, 2.5m), s.Line(3m, 100m, presentation: other) },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var body = await JsonOf(update);
        Assert.Equal("DeliveryNote", body.GetProperty("documentType").GetString());
        Assert.Equal(325m, body.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(2, body.GetProperty("lines").GetArrayLength());
        Assert.Equal("Chorizo", body.GetProperty("lines")[1].GetProperty("productName").GetString());

        var stale = await s.Admin.PutAsJsonAsync($"{Route}/{id}", new
        {
            supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), expectedUpdatedAtUtc = token, lines = new[] { s.Line() },
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("reception-modified", await ErrorOf(stale));
        Assert.Equal(2, (await JsonOf(await s.Admin.GetAsync($"{Route}/{id}"))).GetProperty("lines").GetArrayLength());

        await ConfirmAsync(s, id);
        var frozen = await s.Admin.PutAsJsonAsync($"{Route}/{id}", new
        {
            supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), lines = new[] { s.Line() },
        });
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
        Assert.Equal("reception-not-draft", await ErrorOf(frozen));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PutAsJsonAsync($"{Route}/{Guid.NewGuid()}", new
        {
            supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), lines = new[] { s.Line() },
        })).StatusCode);
    }

    [Theory]
    [InlineData("Weighted", 1.2345, 10, "lines[0].quantity")]
    [InlineData("FixedQuantity", 2.5, 10, "lines[0].quantity")]
    [InlineData("Weighted", 0, 10, "lines[0].quantity")]
    [InlineData("Weighted", 1, -1, "lines[0].unitCost")]
    [InlineData("Weighted", 1, 1.00001, "lines[0].unitCost")]
    public async Task Draft_RefusesQuantitiesAndCostsThatBreakThePresentationRules(string behavior, double quantity, double unitCost, string field)
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync($"rec-invalid-{Guid.NewGuid():N}@example.com", behavior: behavior);

        var response = await s.Admin.PostAsJsonAsync(Route, new
        {
            supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), lines = new[] { s.Line((decimal)quantity, (decimal)unitCost) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonOf(response)).GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Draft_RefusesUnknownSupplier_PresentationOfAnotherBranch_BadDocumentTypeAndDueBeforeTheDocument()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-refusals@example.com");
        Guid foreignPresentation;
        using (var owner = OpenOwner())
        {
            foreignPresentation = Presentation(s.OrganizationId, Branch(owner, s.OrganizationId, "Otra"));
        }

        async Task<JsonElement> Refused(object body)
        {
            var response = await s.Admin.PostAsJsonAsync(Route, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await JsonOf(response)).GetProperty("errors");
        }

        Assert.True((await Refused(new { supplierId = Guid.NewGuid(), documentType = "Invoice", occurredOn = Today(), lines = new[] { s.Line() } })).TryGetProperty("supplierId", out _));
        Assert.True((await Refused(new { supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), lines = new[] { s.Line(presentation: foreignPresentation) } })).TryGetProperty("lines[0].presentationId", out _));
        Assert.True((await Refused(new { supplierId = s.SupplierId, documentType = "Bogus", occurredOn = Today(), lines = new[] { s.Line() } })).TryGetProperty("documentType", out _));
        Assert.True((await Refused(new { supplierId = s.SupplierId, documentType = "Invoice", occurredOn = Today(), dueOn = DaysFromToday(-5), lines = new[] { s.Line() } })).TryGetProperty("dueOn", out _));
    }

    [Fact]
    public async Task Receptions_RequireASelectedBranch_AndTheSupplierPermission()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-access@example.com");

        var noBranch = await SignInAsync("rec-access@example.com");
        var response = await noBranch.GetAsync(Route);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("branch-selection-required", await ErrorOf(response));

        var cashierEmail = await CreateCashierAsync(s.OrganizationId, s.BranchId);
        var cashier = await SignInAsync(cashierEmail, s.BranchId);
        Assert.True((await cashier.GetAsync(Route)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found or HttpStatusCode.Unauthorized);
        Assert.True((await cashier.PostAsJsonAsync(Route, new { supplierId = s.SupplierId })).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Found or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_FiltersByStatusSupplierDatesAndSearch_AndGetReturnsTheLines()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-list@example.com");
        var confirmed = (await CreateDraftAsync(s, documentReference: "FAC-100", occurredOn: DaysFromToday(-10))).GetProperty("id").GetGuid();
        await ConfirmAsync(s, confirmed);
        var draft = (await CreateDraftAsync(s, documentReference: "REM-200", occurredOn: Today(), documentType: "DeliveryNote")).GetProperty("id").GetGuid();

        async Task<Guid[]> Ids(string query) =>
            (await s.Admin.GetFromJsonAsync<JsonElement>($"{Route}{query}")).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal([draft, confirmed], await Ids(""));                       // newest document date first
        Assert.Equal([confirmed], await Ids("?status=Confirmed"));
        Assert.Equal([draft], await Ids("?status=Draft"));
        Assert.Equal([confirmed], await Ids($"?to={DaysFromToday(-5)}"));
        Assert.Equal([draft], await Ids($"?from={DaysFromToday(-5)}"));
        Assert.Equal([confirmed], await Ids("?search=fac-1"));
        Assert.Equal([confirmed], await Ids("?search=R01-W-1"));
        Assert.Equal([draft, confirmed], await Ids($"?supplierId={s.SupplierId}"));
        Assert.Empty(await Ids($"?supplierId={Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync($"{Route}?status=Bogus")).StatusCode);

        var detail = await s.Admin.GetFromJsonAsync<JsonElement>($"{Route}/{confirmed}");
        Assert.Equal("Frigorífico Sur", detail.GetProperty("supplierName").GetString());
        var line = detail.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Media res", line.GetProperty("productName").GetString());
        Assert.Equal("Kg", line.GetProperty("presentationName").GetString());
        Assert.Equal(120m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(480_000m, line.GetProperty("lineTotal").GetDecimal());
        Assert.Equal(1, (await s.Admin.GetFromJsonAsync<JsonElement>(Route + "?status=Draft")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync($"{Route}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ConfirmAndVoid_WriteAuditEntries_AndTheLineTotalIsRoundedPerLine()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-audit@example.com");
        var draft = await CreateDraftAsync(s, [s.Line(2.5m, 1.005m), s.Line(0.333m, 3m)]);
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal(3.51m, draft.GetProperty("totalAmount").GetDecimal()); // 2.51 + 1.00

        await ConfirmAsync(s, id);
        await VoidAsync(s, id);

        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'purchase_reception.confirmed'", id));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'purchase_reception.voided'", id));
    }

    [Fact]
    public async Task Void_WhenTheInvoiceWasAlreadyReversedByHand_StillBringsTheStockBack_WithoutASecondReversal()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-void-manual@example.com");
        var id = (await CreateDraftAsync(s)).GetProperty("id").GetGuid();
        var invoiceId = (await JsonOf(await ConfirmAsync(s, id))).GetProperty("ledgerInvoiceMovementId").GetGuid();

        var manual = await s.Admin.PostAsJsonAsync($"/suppliers/{s.SupplierId}/account/movements/{invoiceId}/reverse", new { });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        var voided = await VoidAsync(s, id);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonOf(voided)).GetProperty("ledgerReversalMovementId").ValueKind);
        Assert.Equal(0m, OnHand(s.Presentation));
        Assert.Equal(0m, await BalanceAsync(s));
        Assert.Equal(2, (await s.Admin.GetFromJsonAsync<JsonElement>($"/suppliers/{s.SupplierId}/account/statement")).GetProperty("movements").GetArrayLength());
    }

    [Fact]
    public async Task ConfirmingTheSameDraftConcurrently_AppliesItExactlyOnce()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("rec-race@example.com");
        var id = (await CreateDraftAsync(s)).GetProperty("id").GetGuid();

        var results = await Task.WhenAll(ConfirmAsync(s, id), ConfirmAsync(s, id), ConfirmAsync(s, id));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(120m, OnHand(s.Presentation));
        Assert.Equal(480_000m, await BalanceAsync(s));
    }

    private async Task<string> CreateCashierAsync(Guid organizationId, Guid branchId)
    {
        var userId = Guid.NewGuid();
        var email = $"cashier-{userId:N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
        var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), Password);
        var outcome = await store.CreateStaffUserAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, [branchId], [new RoleDto("cashier", Permission.OperatePos)]),
            new UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId, "user", userId, "user.created", null, null),
            CancellationToken.None);
        Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        return email;
    }
}
