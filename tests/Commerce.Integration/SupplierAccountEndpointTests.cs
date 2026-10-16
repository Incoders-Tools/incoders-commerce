using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Commerce.Integration;

/// <summary>
/// The supplier current account endpoints (`/suppliers/{id}/account/...`): register a movement, reverse it,
/// the statement by date range, the summary with aging and the balances of the whole organization.
/// Shares the fixture and helpers of <see cref="SupplierEndpointTests"/>.
/// </summary>
public sealed partial class SupplierEndpointTests
{
    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).ToString("yyyy-MM-dd");

    private static string DaysFromToday(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).AddDays(days).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> RegisterAsync(
        HttpClient client, Guid supplierId, string kind, decimal amount, string? occurredOn = null, string? dueOn = null,
        string concept = "Concepto", string? direction = null, string? documentReference = null)
    {
        var response = await client.PostAsJsonAsync($"/suppliers/{supplierId}/account/movements",
            new { kind, amount, occurredOn, dueOn, concept, direction, documentReference });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> SummaryAsync(HttpClient client, Guid supplierId, string? asOf = null) =>
        client.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}/account/summary" + (asOf is null ? "" : $"?asOf={asOf}"));

    private static async Task<HttpResponseMessage> ReverseAsync(HttpClient client, Guid supplierId, Guid movementId, object? body = null) =>
        await client.PostAsJsonAsync($"/suppliers/{supplierId}/account/movements/{movementId}/reverse", body ?? new { });

    private static async Task<string?> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [Fact]
    public async Task AcceptanceScenario_InvoiceAndPartialPayment_ThenReversingThePayment()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-scenario@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Frigorífico Sur", paymentTermsDays: 30));

        var invoice = await RegisterAsync(admin, supplierId, "Invoice", 100_000m, occurredOn: Today(), documentReference: "A-0001-00000123");
        Assert.Equal("Invoice", invoice.GetProperty("kind").GetString());
        Assert.Equal("Credit", invoice.GetProperty("direction").GetString());
        Assert.Equal(DaysFromToday(30), invoice.GetProperty("dueOn").GetString()); // defaulted from the payment terms
        Assert.Equal("A-0001-00000123", invoice.GetProperty("documentReference").GetString());
        Assert.Equal(supplierId, invoice.GetProperty("supplierId").GetGuid());

        var payment = await RegisterAsync(admin, supplierId, "Payment", 40_000m, occurredOn: Today());
        Assert.Equal("Debit", payment.GetProperty("direction").GetString());
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("dueOn").ValueKind);

        var summary = await SummaryAsync(admin, supplierId);
        Assert.Equal(60_000m, summary.GetProperty("balance").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("overdue").GetDecimal());
        Assert.Equal(60_000m, (await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}")).GetProperty("balance").GetDecimal());
        Assert.Equal(60_000m, (await admin.GetFromJsonAsync<JsonElement>("/suppliers")).EnumerateArray().Single().GetProperty("balance").GetDecimal());

        var paymentId = payment.GetProperty("id").GetGuid();
        var reversed = await ReverseAsync(admin, supplierId, paymentId);
        Assert.Equal(HttpStatusCode.Created, reversed.StatusCode);
        var reversal = await reversed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reversal", reversal.GetProperty("kind").GetString());
        Assert.Equal("Credit", reversal.GetProperty("direction").GetString()); // opposite of the Debit payment
        Assert.Equal(40_000m, reversal.GetProperty("amount").GetDecimal());
        Assert.Equal(paymentId, reversal.GetProperty("reversesMovementId").GetGuid());

        Assert.Equal(100_000m, (await SummaryAsync(admin, supplierId)).GetProperty("balance").GetDecimal());

        var statement = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}/account/statement");
        Assert.Equal(0m, statement.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(100_000m, statement.GetProperty("closingBalance").GetDecimal());
        var lines = statement.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(["Invoice", "Payment", "Reversal"], lines.Select(l => l.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal([100_000m, 60_000m, 100_000m], lines.Select(l => l.GetProperty("runningBalance").GetDecimal()).ToArray());
        Assert.True(lines[1].GetProperty("reversed").GetBoolean()); // the reversed payment stays visible, marked as reversed
        Assert.Equal(reversal.GetProperty("id").GetGuid(), lines[1].GetProperty("reversedByMovementId").GetGuid());
        Assert.False(lines[0].GetProperty("reversed").GetBoolean());
        Assert.False(lines[2].GetProperty("reversed").GetBoolean());

        Assert.Equal(2, CountAudit(supplierId, "supplier.movement_registered"));
        Assert.Equal(1, CountAudit(supplierId, "supplier.movement_reversed"));
    }

    [Theory]
    [InlineData(10, "d0_30")]
    [InlineData(45, "d31_60")]
    [InlineData(75, "d61_90")]
    [InlineData(120, "d90plus")]
    public async Task OverdueInvoice_CountsAsOverdue_AndLandsInTheRightAgingBucket(int daysOverdue, string bucket)
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync($"acc-aging-{daysOverdue}@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));
        await RegisterAsync(admin, supplierId, "Invoice", 100_000m, occurredOn: DaysFromToday(-daysOverdue - 30), dueOn: DaysFromToday(-daysOverdue));

        var summary = await SummaryAsync(admin, supplierId);

        Assert.Equal(100_000m, summary.GetProperty("balance").GetDecimal());
        Assert.Equal(100_000m, summary.GetProperty("overdue").GetDecimal());
        var aging = summary.GetProperty("aging");
        foreach (var name in new[] { "d0_30", "d31_60", "d61_90", "d90plus" })
        {
            Assert.Equal(name == bucket ? 100_000m : 0m, aging.GetProperty(name).GetDecimal());
        }

        var balances = await admin.GetFromJsonAsync<JsonElement>("/suppliers/account/balances");
        var entry = balances.EnumerateArray().Single(b => b.GetProperty("supplierId").GetGuid() == supplierId);
        Assert.Equal(100_000m, entry.GetProperty("balance").GetDecimal());
        Assert.Equal(100_000m, entry.GetProperty("overdue").GetDecimal());
    }

    [Fact]
    public async Task Summary_AppliesPaymentsFifo_AndHonoursTheAsOfDate()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-fifo@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));
        await RegisterAsync(admin, supplierId, "Invoice", 100m, occurredOn: "2026-07-01", dueOn: "2026-08-01");
        await RegisterAsync(admin, supplierId, "Invoice", 50m, occurredOn: "2026-08-20", dueOn: "2026-09-15");
        await RegisterAsync(admin, supplierId, "Payment", 120m, occurredOn: "2026-09-20");

        var summary = await SummaryAsync(admin, supplierId, "2026-10-02");
        Assert.Equal(30m, summary.GetProperty("balance").GetDecimal());
        Assert.Equal(30m, summary.GetProperty("overdue").GetDecimal());
        Assert.Equal(30m, summary.GetProperty("aging").GetProperty("d0_30").GetDecimal());

        // Before the payment existed, both invoices were open and the older one was already 30+ days late.
        var earlier = await SummaryAsync(admin, supplierId, "2026-09-10");
        Assert.Equal(150m, earlier.GetProperty("balance").GetDecimal());
        Assert.Equal(100m, earlier.GetProperty("aging").GetProperty("d31_60").GetDecimal());
    }

    [Fact]
    public async Task Statement_ByRange_CarriesOpeningAndClosingBalance()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-statement@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));
        await RegisterAsync(admin, supplierId, "OpeningBalance", 500m, occurredOn: "2026-01-01");
        await RegisterAsync(admin, supplierId, "Invoice", 200m, occurredOn: "2026-02-10");
        await RegisterAsync(admin, supplierId, "Payment", 300m, occurredOn: "2026-02-20");
        await RegisterAsync(admin, supplierId, "Invoice", 50m, occurredOn: "2026-03-05");

        var february = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}/account/statement?from=2026-02-01&to=2026-02-28");
        Assert.Equal(500m, february.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(400m, february.GetProperty("closingBalance").GetDecimal());
        var lines = february.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(["Invoice", "Payment"], lines.Select(l => l.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal([700m, 400m], lines.Select(l => l.GetProperty("runningBalance").GetDecimal()).ToArray());

        var toOnly = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}/account/statement?to=2026-01-31");
        Assert.Equal(0m, toOnly.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(500m, toOnly.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(1, toOnly.GetProperty("movements").GetArrayLength());

        var empty = await admin.GetFromJsonAsync<JsonElement>($"/suppliers/{supplierId}/account/statement?from=2027-01-01");
        Assert.Equal(450m, empty.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(450m, empty.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0, empty.GetProperty("movements").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.GetAsync($"/suppliers/{supplierId}/account/statement?from=2026-03-01&to=2026-02-01")).StatusCode);
    }

    [Fact]
    public async Task Adjustments_GoInTheChosenDirection()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-adjust@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));

        await RegisterAsync(admin, supplierId, "Adjustment", 100m, occurredOn: Today(), direction: "Credit", concept: "Diferencia de cambio");
        Assert.Equal(100m, (await SummaryAsync(admin, supplierId)).GetProperty("balance").GetDecimal());
        await RegisterAsync(admin, supplierId, "Adjustment", 30m, occurredOn: Today(), direction: "Debit");
        await RegisterAsync(admin, supplierId, "CreditNote", 20m, occurredOn: Today());
        Assert.Equal(50m, (await SummaryAsync(admin, supplierId)).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Reverse_RefusesAnAlreadyReversedMovement_AReversal_AnEarlierDate_AndForeignOrUnknownIds()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-reverse@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Uno"));
        var otherSupplierId = await CreateSupplierAsync(admin, SupplierBody("Dos"));
        var invoice = await RegisterAsync(admin, supplierId, "Invoice", 100m, occurredOn: "2026-09-10");
        var invoiceId = invoice.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await ReverseAsync(admin, supplierId, invoiceId, new { occurredOn = "2026-09-01" })).StatusCode);

        var first = await ReverseAsync(admin, supplierId, invoiceId, new { concept = "Factura mal cargada", occurredOn = "2026-09-11" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var reversal = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Factura mal cargada", reversal.GetProperty("concept").GetString());
        Assert.Equal("2026-09-11", reversal.GetProperty("occurredOn").GetString());

        var again = await ReverseAsync(admin, supplierId, invoiceId);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("movement-already-reversed", await ErrorOf(again));

        var reverseTheReversal = await ReverseAsync(admin, supplierId, reversal.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, reverseTheReversal.StatusCode);
        Assert.Equal("movement-not-reversible", await ErrorOf(reverseTheReversal));

        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(admin, otherSupplierId, invoiceId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(admin, supplierId, Guid.NewGuid())).StatusCode);
        Assert.Equal(0m, (await SummaryAsync(admin, supplierId)).GetProperty("balance").GetDecimal());
    }

    [Theory]
    [InlineData("Bogus", 10, "Concepto", null, null, "kind")]
    [InlineData("Reversal", 10, "Concepto", null, null, "kind")]
    [InlineData("Invoice", 0, "Concepto", null, null, "amount")]
    [InlineData("Invoice", -5, "Concepto", null, null, "amount")]
    [InlineData("Invoice", 10.123, "Concepto", null, null, "amount")]
    [InlineData("Invoice", 10, "  ", null, null, "concept")]
    [InlineData("Adjustment", 10, "Concepto", null, null, "direction")]
    [InlineData("Payment", 10, "Concepto", "Credit", null, "direction")]
    [InlineData("Invoice", 10, "Concepto", null, "2026-01-01", "dueOn")]
    [InlineData("Payment", 10, "Concepto", null, "2026-12-31", "dueOn")]
    public async Task Register_WithInvalidFields_Returns400_NamingTheField(
        string kind, double amount, string concept, string? direction, string? dueOn, string field)
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync($"acc-invalid-{kind}-{amount}-{field}-{direction}-{dueOn}@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));

        var response = await admin.PostAsJsonAsync($"/suppliers/{supplierId}/account/movements",
            new { kind, amount, occurredOn = "2026-06-01", dueOn, concept, direction });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AccountRoutes_AreInvisibleToOtherOrganizations_AndRequireManageUsers()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, _) = await BootstrapAsync("acc-perm-a@example.com");
        var adminA = await SignInAsync("acc-perm-a@example.com");
        var adminB = await NewAdminAsync("acc-perm-b@example.com");
        var supplierId = await CreateSupplierAsync(adminA, SupplierBody("Privado"));
        var movement = await RegisterAsync(adminA, supplierId, "Invoice", 10m, occurredOn: Today());

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/suppliers/{supplierId}/account/movements",
            new { kind = "Invoice", amount = 1, concept = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/suppliers/{supplierId}/account/statement")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/suppliers/{supplierId}/account/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReverseAsync(adminB, supplierId, movement.GetProperty("id").GetGuid())).StatusCode);
        Assert.Equal(0, (await adminB.GetFromJsonAsync<JsonElement>("/suppliers/account/balances")).GetArrayLength());

        var cashier = await SignInAsync(await CreateCashierAsync(orgId, branchId), branchId);
        AssertRefused(await cashier.GetAsync($"/suppliers/{supplierId}/account/statement"));
        AssertRefused(await cashier.GetAsync($"/suppliers/{supplierId}/account/summary"));
        AssertRefused(await cashier.GetAsync("/suppliers/account/balances"));
        AssertRefused(await cashier.PostAsJsonAsync($"/suppliers/{supplierId}/account/movements", new { kind = "Invoice", amount = 1, concept = "x" }));
        AssertRefused(await ReverseAsync(cashier, supplierId, movement.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task OccurredOn_DefaultsToToday_AndAMovementCannotBeUpdatedOrDeletedThroughTheApi()
    {
        if (!_postgresAvailable) return;
        var admin = await NewAdminAsync("acc-default@example.com");
        var supplierId = await CreateSupplierAsync(admin, SupplierBody("Proveedor"));

        var movement = await RegisterAsync(admin, supplierId, "Payment", 5m);
        Assert.Equal(Today(), movement.GetProperty("occurredOn").GetString());

        var url = $"/suppliers/{supplierId}/account/movements/{movement.GetProperty("id").GetGuid()}";
        Assert.False((await admin.DeleteAsync(url)).IsSuccessStatusCode);
        Assert.False((await admin.PutAsJsonAsync(url, new { amount = 1 })).IsSuccessStatusCode);
    }
}
