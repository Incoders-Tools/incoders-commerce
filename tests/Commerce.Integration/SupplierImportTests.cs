using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
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
/// Covers commerce-pricing-engine Phase 9 tasks 9.9/9.10/9.11: the full
/// upload -&gt; review -&gt; commit/reject lifecycle through the real HTTP
/// endpoints (design.md "Import lifecycle"). Reuses
/// <c>PricingEndpointTests</c>'s exact sign-in/seed harness. If Postgres is
/// not reachable, these report the gap clearly and return without
/// asserting pass/fail, matching the existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class SupplierImportTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public SupplierImportTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
        });

        if (_postgresAvailable)
        {
            ApplyMigrationsAndReset();
        }
    }

    public void Dispose() => _factory.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();

        void Apply(string file, string? placeholder = null, string? replacement = null)
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", file));
            if (placeholder is not null)
            {
                sql = sql.Replace(placeholder, replacement);
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        Apply("0001_init_rls.sql", "__APP_RUNTIME_PASSWORD__", "dev-only-password");
        Apply("0002_users.sql");
        Apply("0003_organizations_branches.sql");
        Apply("0004_device_credentials.sql");
        Apply("0009_catalog_and_pricing.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE price_import_rows, price_import_batches, supplier_price_mappings, " +
            "price_list_entries, price_lists, presentations, products, " +
            "user_directory, users, device_credentials, branches, organizations CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedPresentationWithCodeAsync(Guid organizationId, string identificationCode)
    {
        using var scope = _factory.Services.CreateScope();
        var catalogStore = scope.ServiceProvider.GetRequiredService<PostgresCatalogStore>();
        var tenantScope = new CloudTenantScope(organizationId);
        var actorId = Guid.NewGuid();

        var product = await catalogStore.CreateProductAsync(
            tenantScope, new NewProduct(Guid.NewGuid(), "Seed Product", Guid.NewGuid(), Guid.NewGuid(), actorId),
            "org-user", actorId, CancellationToken.None);

        var presentation = await catalogStore.CreatePresentationAsync(
            tenantScope,
            new NewPresentation(Guid.NewGuid(), product.Id, "Seed Presentation", QuantityBehavior.FixedQuantity, Guid.NewGuid(), identificationCode, actorId),
            "org-user", actorId, CancellationToken.None);

        return presentation.Id;
    }

    private async Task<(HttpClient client, Guid organizationId)> SignedInClientAsync(Permission permissions)
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await SeedOrganizationAsync(organizationId);

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var newUser = new NewUserAccount(
                userId, $"{userId}@example.com", "unused-hash", new[] { branchId }, new[] { new RoleDto("test-role", permissions) });
            var tenantScope = new CloudTenantScope(organizationId);
            var created = await store.TryCreateAsync(tenantScope, newUser, CancellationToken.None);
            Assert.True(created);
        }

        return (await SignInViaTestEndpointAsync(organizationId, userId), organizationId);
    }

    private async Task<HttpClient> SignInViaTestEndpointAsync(Guid organizationId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), "test-password");
        using var cmd = new NpgsqlCommand("UPDATE users SET password_hash = $1 WHERE id = $2", owner);
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();

        using var emailCmd = new NpgsqlCommand("SELECT email FROM users WHERE id = $1", owner);
        emailCmd.Parameters.AddWithValue(userId);
        var email = (string)(await emailCmd.ExecuteScalarAsync())!;

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, "test-password"));
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
        return client;
    }

    private static byte[] BuildWorkbook(params (string code, decimal price)[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Prices");
        sheet.Cell(1, "A").Value = "Code";
        sheet.Cell(1, "B").Value = "Price";
        for (var i = 0; i < rows.Length; i++)
        {
            sheet.Cell(i + 2, "A").Value = rows[i].code;
            sheet.Cell(i + 2, "B").Value = rows[i].price;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<Guid> CreateSupplierMappingAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/pricing/supplier-mappings", new
        {
            supplierName = "Acme",
            sheetName = "Prices",
            headerRow = 1,
            codeColumn = "A",
            priceColumn = "B",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid supplierMappingId, byte[] fileBytes)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(fileContent, "file", "prices.xlsx");
        form.Add(new StringContent(supplierMappingId.ToString()), "supplierMappingId");
        return await client.PostAsync("/pricing/imports", form);
    }

    private async Task<long> CountPriceListEntriesAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM price_list_entries", connection);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAuditRowsAsync(Guid entityId, string action)
    {
        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = $2", connection);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Upload_StagesTheBatch_AndChangesZeroLivePrices()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, orgId) = await SignedInClientAsync(Permission.ManageCatalog);
        var presentationId = await SeedPresentationWithCodeAsync(orgId, "ABC-123");
        var mappingId = await CreateSupplierMappingAsync(client);
        var fileBytes = BuildWorkbook(("ABC-123", 24.99m), ("GHOST-CODE", 5.00m));

        var response = await UploadAsync(client, mappingId, fileBytes);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Staged", batch.GetProperty("status").GetString());
        Assert.Equal(0, await CountPriceListEntriesAsync());
        _ = presentationId;
    }

    [Fact]
    public async Task Upload_MalformedFile_CreatesNoBatchRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, _) = await SignedInClientAsync(Permission.ManageCatalog);
        var mappingId = await CreateSupplierMappingAsync(client);
        var notAWorkbook = System.Text.Encoding.UTF8.GetBytes("this is not an xlsx file");

        var response = await UploadAsync(client, mappingId, notAWorkbook);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM price_import_batches", connection);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Commit_WritesThroughNormalPath_AndCommittingTwiceReturns409()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, orgId) = await SignedInClientAsync(Permission.ManageCatalog);
        await SeedPresentationWithCodeAsync(orgId, "ABC-123");
        var mappingId = await CreateSupplierMappingAsync(client);
        var fileBytes = BuildWorkbook(("ABC-123", 24.99m));
        var createPriceListResponse = await client.PostAsJsonAsync("/pricing/price-lists", new { name = "Default", isDefault = true });
        Assert.Equal(HttpStatusCode.Created, createPriceListResponse.StatusCode);

        var uploadResponse = await UploadAsync(client, mappingId, fileBytes);
        var batch = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var batchId = batch.GetProperty("id").GetGuid();

        var commitResponse = await client.PostAsync($"/pricing/imports/{batchId}/commit", content: null);
        Assert.Equal(HttpStatusCode.OK, commitResponse.StatusCode);
        Assert.Equal(1, await CountPriceListEntriesAsync());

        var auditRowCount = await CountAuditRowsAsync(batchId, "price-import-batch.committed");
        Assert.Equal(1, auditRowCount);

        var secondCommitResponse = await client.PostAsync($"/pricing/imports/{batchId}/commit", content: null);
        Assert.Equal(HttpStatusCode.Conflict, secondCommitResponse.StatusCode);
        Assert.Equal(1, await CountPriceListEntriesAsync()); // still exactly one — no double-write
    }

    [Fact]
    public async Task Reject_WritesNoPrice_AndMarksBatchRejected()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, orgId) = await SignedInClientAsync(Permission.ManageCatalog);
        await SeedPresentationWithCodeAsync(orgId, "ABC-123");
        var mappingId = await CreateSupplierMappingAsync(client);
        var fileBytes = BuildWorkbook(("ABC-123", 24.99m));

        var uploadResponse = await UploadAsync(client, mappingId, fileBytes);
        var batch = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var batchId = batch.GetProperty("id").GetGuid();

        var rejectResponse = await client.PostAsync($"/pricing/imports/{batchId}/reject", content: null);

        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
        Assert.Equal(0, await CountPriceListEntriesAsync());

        var getResponse = await client.GetAsync($"/pricing/imports/{batchId}");
        var detail = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rejected", detail.GetProperty("batch").GetProperty("status").GetString());
    }
}
