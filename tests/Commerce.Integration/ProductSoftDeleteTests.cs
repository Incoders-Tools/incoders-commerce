using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// `0036_product_soft_delete.sql` and `POST /catalog/products/{id}/deactivate|reactivate`: a deactivated product leaves the default lists,
/// new receptions, the guest catalog and the POS replica, and keeps its history; reactivating brings it back.
/// </summary>
[Collection("Postgres")]
public sealed class ProductSoftDeleteTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public ProductSoftDeleteTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrations();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrations()
    {
        using var owner = OpenOwner();
        foreach (var file in Directory.GetFiles(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations"), "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            PostgresTestFixture.ApplyMigration(owner, file);
    }

    private sealed record Scenario(HttpClient Admin, Guid OrganizationId, Guid BranchId, Guid ProductId, Guid PresentationId);

    private async Task<Scenario> NewScenarioAsync(string label)
    {
        var email = $"{label}-{Guid.NewGuid():N}@example.com";
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var bootstrap = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "Ruta 51", email, Password));
        bootstrap.EnsureSuccessStatusCode();
        var body = (await bootstrap.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        var admin = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        (await admin.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, body.BranchId.ToString());

        var presentationId = Presentation(organizationId, body.BranchId, "Asado", "Por kg");
        Guid productId;
        using (var owner = OpenOwner())
        {
            productId = Scalar<Guid>(owner, "SELECT product_id FROM presentations WHERE id = $1", presentationId);
        }
        return new Scenario(admin, organizationId, body.BranchId, productId, presentationId);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Guid[]> Ids(HttpClient client, string url, string idProperty = "id") =>
        (await client.GetFromJsonAsync<JsonElement>(url)).EnumerateArray().Select(e => e.GetProperty(idProperty).GetGuid()).ToArray();

    [Fact]
    public void Migration_AddsActiveByDefault_AndRerunsSafely_AndTheDevSnapshotCarriesItVerbatim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, "0036_product_soft_delete.sql");
        PostgresTestFixture.ApplyMigration(owner, "0036_product_soft_delete.sql");

        var organizationId = Guid.NewGuid();
        Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Soft delete migration')", organizationId);
        var presentation = Presentation(organizationId, Branch(owner, organizationId), "Default active");
        Assert.True(Scalar<bool>(owner, "SELECT is_active FROM products WHERE id = (SELECT product_id FROM presentations WHERE id = $1)", presentation));
        Assert.Null(Scalar<object>(owner, "SELECT deactivated_at_utc FROM products WHERE id = (SELECT product_id FROM presentations WHERE id = $1)", presentation));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0036_product_soft_delete.sql"))), init);
    }

    [Fact]
    public async Task Deactivate_HidesTheProductFromDefaultLists_KeepsItWithIncludeInactive_ReactivateBringsItBack_AndBothAreAudited()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("soft-delete-lists");

        Assert.True((await Json(await s.Admin.GetAsync($"/catalog/products/{s.ProductId}"))).GetProperty("isActive").GetBoolean());

        var deactivate = await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False((await Json(deactivate)).GetProperty("isActive").GetBoolean());

        Assert.DoesNotContain(s.ProductId, await Ids(s.Admin, "/catalog/products"));
        Assert.DoesNotContain(s.PresentationId, await Ids(s.Admin, "/catalog/presentations"));
        Assert.Contains(s.ProductId, await Ids(s.Admin, "/catalog/products?includeInactive=true"));
        Assert.Contains(s.PresentationId, await Ids(s.Admin, "/catalog/presentations?includeInactive=true"));
        Assert.False((await Json(await s.Admin.GetAsync($"/catalog/products/{s.ProductId}"))).GetProperty("isActive").GetBoolean());

        var reactivate = await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/reactivate", null);
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        Assert.True((await Json(reactivate)).GetProperty("isActive").GetBoolean());
        Assert.Contains(s.ProductId, await Ids(s.Admin, "/catalog/products"));
        Assert.Contains(s.PresentationId, await Ids(s.Admin, "/catalog/presentations"));

        using var owner = OpenOwner();
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'product.deactivated'", s.ProductId));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'product.reactivated'", s.ProductId));
    }

    [Fact]
    public async Task DeactivateAndReactivate_UnknownProduct_Is404_AndTheyAreIdempotent()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("soft-delete-404@example.com");

        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PostAsync($"/catalog/products/{Guid.NewGuid()}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.PostAsync($"/catalog/products/{Guid.NewGuid()}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task InactiveProduct_CannotBeAddedToANewReception_NorGetANewPresentation()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("soft-delete-reception");
        var supplier = await s.Admin.PostAsJsonAsync("/suppliers", new { displayName = "Frigorifico", taxIdType = "None", taxCondition = "ResponsableInscripto", paymentTermsDays = 30 });
        supplier.EnsureSuccessStatusCode();
        var supplierId = (await Json(supplier)).GetProperty("supplierId").GetGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).ToString("yyyy-MM-dd");
        object Draft() => new
        {
            supplierId, documentType = "Invoice", occurredOn = today,
            lines = new[] { new { presentationId = s.PresentationId, quantity = 10m, unitCost = 4000m, lotCode = (string?)null } },
        };

        Assert.Equal(HttpStatusCode.Created, (await s.Admin.PostAsJsonAsync("/purchases/receptions", Draft())).StatusCode);
        (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null)).EnsureSuccessStatusCode();

        var refused = await s.Admin.PostAsJsonAsync("/purchases/receptions", Draft());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await Json(refused)).GetProperty("errors").TryGetProperty("lines[0].presentationId", out _));

        var presentation = await s.Admin.PostAsJsonAsync("/catalog/presentations",
            new { productId = s.ProductId, name = "Pieza", quantityBehavior = Commerce.Domain.Catalog.QuantityBehavior.Weighted, unitId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, presentation.StatusCode);
    }

    [Fact]
    public async Task PosReplica_SendsARemovalForADeactivatedProduct_AndTheRowAgainOnReactivation()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("soft-delete-replica");
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            token = (await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>().IssueAsync(
                new CloudTenantScope(s.OrganizationId), Guid.NewGuid(), s.BranchId, Guid.NewGuid(), CancellationToken.None)).PlaintextToken;
        }

        async Task<JsonElement> Sync(DateTimeOffset since)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/device/catalog/sync?since={Uri.EscapeDataString(since.ToString("O"))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await Json(response);
        }

        static Guid[] Items(JsonElement sync) => sync.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("presentationId").GetGuid()).ToArray();
        static Guid[] Removed(JsonElement sync) => sync.GetProperty("removedPresentationIds").EnumerateArray().Select(e => e.GetGuid()).ToArray();

        var cursor = (await Sync(DateTimeOffset.UnixEpoch)).GetProperty("serverTimeUtc").GetDateTimeOffset();
        await Task.Delay(50);

        (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null)).EnsureSuccessStatusCode();
        var afterDeactivate = await Sync(cursor);
        Assert.Empty(Items(afterDeactivate));
        Assert.Contains(s.PresentationId, Removed(afterDeactivate));
        Assert.DoesNotContain(s.PresentationId, Items(await Sync(DateTimeOffset.UnixEpoch)));

        cursor = afterDeactivate.GetProperty("serverTimeUtc").GetDateTimeOffset();
        await Task.Delay(50);
        (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/reactivate", null)).EnsureSuccessStatusCode();
        var afterReactivate = await Sync(cursor);
        Assert.Contains(s.PresentationId, Items(afterReactivate));
        Assert.DoesNotContain(s.PresentationId, Removed(afterReactivate));
    }

    [Fact]
    public async Task Stock_ListsAnInactiveProductOnlyWhileItStillHoldsStock()
    {
        if (!_postgresAvailable) return;
        var s = await NewScenarioAsync("soft-delete-stock");
        (await s.Admin.PostAsync($"/catalog/products/{s.ProductId}/deactivate", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(s.PresentationId, await Ids(s.Admin, "/stock", "presentationId"));

        (await s.Admin.PostAsJsonAsync("/stock/adjustments", new { presentationId = s.PresentationId, kind = "Opening", quantity = 5, reason = "x" })).EnsureSuccessStatusCode();
        Assert.Contains(s.PresentationId, await Ids(s.Admin, "/stock", "presentationId"));
    }
}
