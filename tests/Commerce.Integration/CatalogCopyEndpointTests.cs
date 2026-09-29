using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// B7 U5b (catalog-item-identification spec "Copying Catalog Between
/// Branches"): <c>POST /catalog/copy</c>. Fixture mirrors
/// <see cref="BranchSelectionTests"/> (bootstrap + sysadmin promotion via raw
/// SQL) and <see cref="CatalogEndpointTests"/> (migration set through 0017,
/// which this feature needs for both the catalog AND pricing branch-ownership
/// RLS policies it reads/writes under).
/// </summary>
[Collection("Postgres")]
public sealed class CatalogCopyEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string OrganizationSelectorHeader = "X-Organization-Id";
    private const string BranchSelectorHeader = "X-Branch-Id";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogCopyEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (_postgresAvailable)
        {
            ApplyMigrationsAndReset();
        }
    }

    public void Dispose()
    {
        _factory.Dispose();

        // A copy test legitimately leaves the SAME identification code in two
        // branches of one organization. Later fixtures re-apply 0009 without
        // truncating first, and 0009's org-wide `presentations_org_code_uk`
        // cannot be recreated over that data — so leave the shared
        // `commerce_test` catalog tables empty for whoever runs next.
        if (_postgresAvailable)
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var cmd = new NpgsqlCommand(
                "TRUNCATE TABLE price_import_rows, price_import_batches, supplier_price_mappings, " +
                "price_list_entries, price_lists, presentations, products CASCADE", owner);
            cmd.ExecuteNonQuery();
        }
    }

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Commerce.sln")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    /// <summary>
    /// Mirrors <c>CatalogEndpointTests</c>'s exact workaround, not
    /// <c>BranchSelectionTests</c>'s plain "run every file in order": this
    /// class seeds real catalog/pricing rows, and `commerce_test` is shared
    /// and accumulating across every test class in the "Postgres" collection.
    /// A PRIOR class may have already run 0016/0017 and left presentations
    /// with the SAME identification_code in two branches of one org — legal
    /// under 0016, but fatal to 0009's org-wide
    /// `presentations_org_code_uk` when THIS run re-creates that index from
    /// scratch (0009 runs before 0016 drops it again). Truncating the
    /// catalog tables immediately before 0009 re-applies means that leftover
    /// data never has to satisfy the org-wide index while it is briefly
    /// reinstated.
    /// </summary>
    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var migrationsDir = Path.Combine(RepoRoot(), "deploy", "db", "migrations");
        var files = Directory.GetFiles(migrationsDir, "*.sql").OrderBy(Path.GetFileName).ToList();

        foreach (var file in files)
        {
            if (Path.GetFileName(file) == "0009_catalog_and_pricing.sql")
            {
                // Same rationale for `price_lists_one_default` (org-wide,
                // Part B of 0009 — 0017 later replaces it with a per-branch
                // version) as `presentations_org_code_uk` above: a prior
                // class in this shared/accumulating database may have left
                // two branches of one org each with their own `is_default`
                // list, legal under 0017 but fatal to 0009's org-wide
                // version being recreated from scratch here.
                using var truncateCatalogCmd = new NpgsqlCommand(
                    "TRUNCATE TABLE price_import_rows, price_import_batches, supplier_price_mappings, " +
                    "price_list_entries, price_lists, presentations, products CASCADE", owner);
                try { truncateCatalogCmd.ExecuteNonQuery(); } catch (PostgresException) { /* first run: tables don't exist yet */ }
            }

            var sql = File.ReadAllText(file)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var reset = new NpgsqlCommand(
            """
            TRUNCATE TABLE audit_log, price_import_rows, price_import_batches, supplier_price_mappings,
                price_list_entries, price_lists, presentations, products, user_directory, users, branches, organizations
            CASCADE
            """, owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email, string password)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "Ruta 51", email, password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Guid> AddBranchAsync(Guid organizationId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        var branchId = Guid.NewGuid();
        await orgStore.CreateBranchAsync(new CloudTenantScope(organizationId), new NewBranch(branchId, name), CancellationToken.None);
        return branchId;
    }

    private static void WidenBranchScope(Guid userId, Guid branchId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("UPDATE users SET branch_scope = branch_scope || $1::uuid WHERE id = $2", owner);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }

    private static void PromoteToSystemAdmin(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();
    }

    private static void SetBranchHeader(HttpClient client, Guid branchId)
    {
        client.DefaultRequestHeaders.Remove(BranchSelectorHeader);
        client.DefaultRequestHeaders.Add(BranchSelectorHeader, branchId.ToString());
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/catalog/products", new { name, categoryId = Guid.NewGuid(), defaultUnitId = Guid.NewGuid() });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreatePresentationAsync(HttpClient client, Guid productId, string name, string? code)
    {
        var response = await client.PostAsJsonAsync("/catalog/presentations", new
        {
            productId,
            name,
            quantityBehavior = QuantityBehavior.FixedQuantity,
            unitId = Guid.NewGuid(),
            identificationCode = code,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreatePriceListAsync(HttpClient client, string name, bool isDefault)
    {
        var response = await client.PostAsJsonAsync("/pricing/price-lists", new { name, isDefault });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task AppendPriceEntryAsync(HttpClient client, Guid priceListId, Guid presentationId, decimal unitPrice, DateOnly effectiveFrom)
    {
        var response = await client.PostAsJsonAsync($"/pricing/price-lists/{priceListId}/entries", new { presentationId, unitPrice, effectiveFrom });
        response.EnsureSuccessStatusCode();
    }

    // --- Whole catalog copy -------------------------------------------------

    [Fact]
    public async Task WholeCatalogCopy_SeedsTargetBranch_WithProductsPresentationsCodes_AndLatestPriceList_AndSetsDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-whole-admin@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-whole-admin@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var productId = await CreateProductAsync(admin, "Yerba Mate 1kg");
        var presentationId = await CreatePresentationAsync(admin, productId, "1kg bag", "7791234567890");
        var priceListId = await CreatePriceListAsync(admin, "Lista general", isDefault: true);
        await AppendPriceEntryAsync(admin, priceListId, presentationId, 1500m, new DateOnly(2026, 1, 1));
        await AppendPriceEntryAsync(admin, priceListId, presentationId, 1600m, new DateOnly(2026, 6, 1));

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("productsCopied").GetInt32());
        Assert.Equal(1, result.GetProperty("presentationsCopied").GetInt32());
        Assert.Equal(0, result.GetProperty("skipped").GetArrayLength());
        Assert.Equal(2, result.GetProperty("priceEntriesCopied").GetInt32());
        var newPriceListId = result.GetProperty("priceListId").GetGuid();

        // Centro now holds its own copy, visible only when Centro is selected.
        SetBranchHeader(admin, centroId);
        var centroProducts = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/catalog/products");
        Assert.Equal(1, centroProducts.GetArrayLength());
        var centroProductId = centroProducts[0].GetProperty("id").GetGuid();
        Assert.NotEqual(productId, centroProductId); // a NEW, independent product

        var centroPresentations = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/catalog/presentations");
        Assert.Equal(1, centroPresentations.GetArrayLength());
        var centroPresentationId = centroPresentations[0].GetProperty("id").GetGuid();
        Assert.NotEqual(presentationId, centroPresentationId);
        Assert.Equal("7791234567890", centroPresentations[0].GetProperty("identificationCode").GetString());

        // The copied list became Centro's default (Centro had none).
        var centroPriceLists = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/pricing/price-lists");
        Assert.Equal(1, centroPriceLists.GetArrayLength());
        Assert.Equal(newPriceListId, centroPriceLists[0].GetProperty("id").GetGuid());
        Assert.True(centroPriceLists[0].GetProperty("isDefault").GetBoolean());

        var history = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/pricing/price-lists/{newPriceListId}/presentations/{centroPresentationId}/history");
        Assert.Equal(2, history.GetArrayLength());

        // Independence after edit: renaming the Centro product must not touch Ruta 51's.
        var renamed = await admin.PostAsJsonAsync($"/catalog/products/{centroProductId}/rename", new
        {
            newName = "Renamed only in Centro",
            isOffline = false,
            correlationId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var rutaProduct = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/catalog/products/{productId}");
        Assert.Equal("Yerba Mate 1kg", rutaProduct.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WholeCatalogCopy_SourceHasNoPriceList_CopiesProductsOnly_AndReportsZeroPriceEntries()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-nopricelist@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-nopricelist@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var productId = await CreateProductAsync(admin, "No Price Product");
        await CreatePresentationAsync(admin, productId, "unit", "1112223334445");

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("productsCopied").GetInt32());
        Assert.Equal(0, result.GetProperty("priceEntriesCopied").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("priceListId").ValueKind);
    }

    // --- Individual copy with a conflicting code ----------------------------

    [Fact]
    public async Task IndividualCopy_ConflictingIdentificationCode_IsSkippedAndReported_RestStillCopied()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-conflict@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-conflict@example.com", "correct-password");

        // A conflicting presentation already exists in Centro.
        SetBranchHeader(admin, centroId);
        var centroExistingProduct = await CreateProductAsync(admin, "Already In Centro");
        await CreatePresentationAsync(admin, centroExistingProduct, "existing", "9998887776665");

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var productId = await CreateProductAsync(admin, "Two Presentations");
        var conflictingPresentationId = await CreatePresentationAsync(admin, productId, "conflicting", "9998887776665");
        var okPresentationId = await CreatePresentationAsync(admin, productId, "ok", "1231231231230");

        var response = await admin.PostAsJsonAsync("/catalog/copy", new
        {
            sourceBranchId = rutaCincuentaYUnoId,
            targetBranchId = centroId,
            productIds = new[] { productId },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("productsCopied").GetInt32());
        Assert.Equal(1, result.GetProperty("presentationsCopied").GetInt32());
        var skipped = result.GetProperty("skipped");
        Assert.Equal(1, skipped.GetArrayLength());
        Assert.Equal(conflictingPresentationId, skipped[0].GetProperty("presentationId").GetGuid());
        Assert.Equal("9998887776665", skipped[0].GetProperty("identificationCode").GetString());

        SetBranchHeader(admin, centroId);
        var centroPresentations = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/catalog/presentations");
        // The pre-existing one, plus exactly the ONE non-conflicting copy.
        Assert.Equal(2, centroPresentations.GetArrayLength());
        _ = okPresentationId;
    }

    [Fact]
    public async Task IndividualCopy_ProductWhoseOnlyPresentationConflicts_IsNotCreatedInTarget()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-allskipped@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-allskipped@example.com", "correct-password");

        SetBranchHeader(admin, centroId);
        var centroExistingProduct = await CreateProductAsync(admin, "Already In Centro");
        await CreatePresentationAsync(admin, centroExistingProduct, "existing", "5556667778889");

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var productId = await CreateProductAsync(admin, "Fully Conflicting");
        await CreatePresentationAsync(admin, productId, "dup", "5556667778889");

        var response = await admin.PostAsJsonAsync("/catalog/copy", new
        {
            sourceBranchId = rutaCincuentaYUnoId,
            targetBranchId = centroId,
            productIds = new[] { productId },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, result.GetProperty("productsCopied").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped").GetArrayLength());

        SetBranchHeader(admin, centroId);
        var centroProducts = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/catalog/products");
        Assert.Equal(1, centroProducts.GetArrayLength()); // only the pre-existing one
    }

    // --- Authorization -------------------------------------------------------

    [Fact]
    public async Task Seller_WithoutManageCatalog_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-seller-admin@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);

        var sellerId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<Commerce.Domain.Identity.UserAccount>>();
            var hash = hasher.HashPassword(new Commerce.Domain.Identity.UserAccount(sellerId, organizationId, [], []), "correct-password");
            var tenantScope = new CloudTenantScope(organizationId);
            var outcome = await store.CreateStaffUserAsync(
                tenantScope,
                new NewUserAccount(sellerId, "copy-seller@example.com", hash, [rutaCincuentaYUnoId], [new RoleDto("seller", Commerce.Domain.Identity.Permission.ViewSales)]),
                new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry("org-user", adminId, organizationId, "user", sellerId, "user.created", null, null),
                CancellationToken.None);
            Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        }

        var seller = await SignInAsync("copy-seller@example.com", "correct-password");
        SetBranchHeader(seller, rutaCincuentaYUnoId);

        var response = await seller.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TargetBranch_OfADifferentOrganization_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, rutaCincuentaYUnoId, _) = await BootstrapAsync("copy-crossorg-a@example.com", "correct-password");
        var (_, otherOrgBranchId, _) = await BootstrapAsync("copy-crossorg-b@example.com", "correct-password");
        var admin = await SignInAsync("copy-crossorg-a@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = otherOrgBranchId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TargetBranch_OutOfCallersScope_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, _) = await BootstrapAsync("copy-outofscope@example.com", "correct-password");
        // Centro exists in the SAME org, but the admin's BranchScope is never widened to include it.
        var centroId = await AddBranchAsync(organizationId, "Centro");
        var admin = await SignInAsync("copy-outofscope@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SelectedBranchHeader_MustEqualSourceBranch_Returns400_WhenItDoesNot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-headermismatch@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-headermismatch@example.com", "correct-password");
        SetBranchHeader(admin, centroId); // selected branch is Centro, but the body claims Ruta 51 is the source

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sysadmin_ActingOnSelectedOrganization_CanCopyBetweenItsBranches()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, _, sysadminId) = await BootstrapAsync("copy-sysadmin@example.com", "correct-password");
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, rutaCincuentaYUnoId, _) = await BootstrapAsync("copy-sysadmin-target@example.com", "correct-password");
        var centroId = await AddBranchAsync(targetOrgId, "Centro");
        var sysadmin = await SignInAsync("copy-sysadmin@example.com", "correct-password");

        sysadmin.DefaultRequestHeaders.Add(OrganizationSelectorHeader, targetOrgId.ToString());
        SetBranchHeader(sysadmin, rutaCincuentaYUnoId);
        var productId = await CreateProductAsync(sysadmin, "Sysadmin Seeded Product");
        await CreatePresentationAsync(sysadmin, productId, "unit", null);

        var response = await sysadmin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("productsCopied").GetInt32());
    }

    // --- Atomicity ------------------------------------------------------------

    [Fact]
    public async Task Copy_WithOneNonexistentProductIdAfterAValidOne_WritesNothing()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-atomic@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-atomic@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var validProductId = await CreateProductAsync(admin, "Valid Product");
        await CreatePresentationAsync(admin, validProductId, "unit", "3213214214210");
        var nonexistentProductId = Guid.NewGuid();

        // Requested in this ORDER on purpose: the store copies products in
        // the caller's own request order, so `validProductId` is fully
        // staged (uncommitted) inside the SAME transaction before the
        // nonexistent id is reached and the whole call throws — proving the
        // transaction rolls back everything, not just the failing item.
        var response = await admin.PostAsJsonAsync("/catalog/copy", new
        {
            sourceBranchId = rutaCincuentaYUnoId,
            targetBranchId = centroId,
            productIds = new[] { validProductId, nonexistentProductId },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        SetBranchHeader(admin, centroId);
        var centroProducts = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/catalog/products");
        Assert.Equal(0, centroProducts.GetArrayLength());
    }

    // --- Audit ------------------------------------------------------------

    [Fact]
    public async Task Copy_WritesOneAuditRow_WithActorSourceTargetAndCounts()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-audit@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-audit@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var productId = await CreateProductAsync(admin, "Audited Product");
        await CreatePresentationAsync(admin, productId, "unit", "1112223334446");

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_id, action, new_value FROM audit_log WHERE action = 'catalog.copied' AND organization_id = $1", owner);
        cmd.Parameters.AddWithValue(organizationId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(adminId, reader.GetGuid(0));
        // `jsonb` normalizes formatting/key order on read (a space after
        // `:`, keys not necessarily in insertion order) — parse it rather
        // than asserting the raw text a caller happened to insert.
        var newValue = System.Text.Json.JsonDocument.Parse(reader.GetString(2)).RootElement;
        Assert.Equal(rutaCincuentaYUnoId, newValue.GetProperty("sourceBranchId").GetGuid());
        Assert.Equal(centroId, newValue.GetProperty("targetBranchId").GetGuid());
        Assert.Equal(1, newValue.GetProperty("productsCopied").GetInt32());
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task Copy_AuditRow_RecordsSkippedCount_WhenACodeAlreadyExistsInTarget()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-audit-skip@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-audit-skip@example.com", "correct-password");

        SetBranchHeader(admin, centroId);
        var existing = await CreateProductAsync(admin, "Already in Centro");
        await CreatePresentationAsync(admin, existing, "unit", "5550001110001");

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var clash = await CreateProductAsync(admin, "Clashing");
        await CreatePresentationAsync(admin, clash, "unit", "5550001110001");
        var fresh = await CreateProductAsync(admin, "Fresh");
        await CreatePresentationAsync(admin, fresh, "unit", "5550001110002");

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT new_value FROM audit_log WHERE action = 'catalog.copied' AND organization_id = $1", owner);
        cmd.Parameters.AddWithValue(organizationId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var newValue = System.Text.Json.JsonDocument.Parse(reader.GetString(0)).RootElement;
        Assert.Equal(1, newValue.GetProperty("productsCopied").GetInt32());
        Assert.Equal(1, newValue.GetProperty("presentationsCopied").GetInt32());
        Assert.Equal(1, newValue.GetProperty("skippedCount").GetInt32());
    }

    // --- Price list carry ---------------------------------------------------

    [Fact]
    public async Task Copy_CarriesOnlyTheLatestCreatedPriceList_AndOnlyEntriesOfCopiedPresentations()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-latest@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-latest@example.com", "correct-password");

        SetBranchHeader(admin, centroId);
        var existing = await CreateProductAsync(admin, "Already in Centro");
        await CreatePresentationAsync(admin, existing, "unit", "6660001110001");

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var clash = await CreateProductAsync(admin, "Clashing");
        var clashPresentation = await CreatePresentationAsync(admin, clash, "unit", "6660001110001");
        var fresh = await CreateProductAsync(admin, "Fresh");
        var freshPresentation = await CreatePresentationAsync(admin, fresh, "unit", "6660001110002");
        var older = await CreatePriceListAsync(admin, "Older list", isDefault: true);
        await AppendPriceEntryAsync(admin, older, freshPresentation, 10m, new DateOnly(2026, 1, 1));
        var newer = await CreatePriceListAsync(admin, "Newer list", isDefault: false);
        await AppendPriceEntryAsync(admin, newer, freshPresentation, 20m, new DateOnly(2026, 2, 1));
        await AppendPriceEntryAsync(admin, newer, clashPresentation, 30m, new DateOnly(2026, 2, 1));

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, result.GetProperty("priceEntriesCopied").GetInt32());

        SetBranchHeader(admin, centroId);
        var lists = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/pricing/price-lists");
        Assert.Equal(1, lists.GetArrayLength());
        Assert.Equal("Newer list", lists[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Copy_TargetWithDefaultList_KeepsItsListsUntouched_AndCopiedListIsNotDefault()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-target-default@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-target-default@example.com", "correct-password");

        SetBranchHeader(admin, centroId);
        var centroDefault = await CreatePriceListAsync(admin, "Centro default", isDefault: true);

        SetBranchHeader(admin, rutaCincuentaYUnoId);
        var product = await CreateProductAsync(admin, "Priced");
        var presentation = await CreatePresentationAsync(admin, product, "unit", "7770001110001");
        var source = await CreatePriceListAsync(admin, "Ruta list", isDefault: true);
        await AppendPriceEntryAsync(admin, source, presentation, 99m, new DateOnly(2026, 3, 1));

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var copiedId = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("priceListId").GetGuid();

        SetBranchHeader(admin, centroId);
        var lists = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/pricing/price-lists");
        Assert.Equal(2, lists.GetArrayLength());
        foreach (var list in lists.EnumerateArray())
        {
            var id = list.GetProperty("id").GetGuid();
            Assert.Equal(id == centroDefault, list.GetProperty("isDefault").GetBoolean());
            Assert.True(id == centroDefault || id == copiedId);
        }
    }

    [Fact]
    public async Task Copy_RanksAListThatReceivedALaterImport_AsTheLatestUploaded()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (organizationId, rutaCincuentaYUnoId, adminId) = await BootstrapAsync("copy-import-latest@example.com", "correct-password");
        var centroId = await AddBranchAsync(organizationId, "Centro");
        WidenBranchScope(adminId, centroId);
        var admin = await SignInAsync("copy-import-latest@example.com", "correct-password");
        SetBranchHeader(admin, rutaCincuentaYUnoId);

        var product = await CreateProductAsync(admin, "Imported");
        var presentation = await CreatePresentationAsync(admin, product, "unit", "8880001110001");
        var first = await CreatePriceListAsync(admin, "First list", isDefault: true);
        await AppendPriceEntryAsync(admin, first, presentation, 10m, new DateOnly(2026, 1, 1));
        var second = await CreatePriceListAsync(admin, "Second list", isDefault: false);
        await AppendPriceEntryAsync(admin, second, presentation, 20m, new DateOnly(2026, 1, 1));

        // A supplier import committed into the FIRST list after the second was created.
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var cmd = new NpgsqlCommand(
                """
                INSERT INTO price_list_entries
                    (id, organization_id, branch_id, price_list_id, presentation_id, unit_price, effective_from, source, created_by_user_id)
                SELECT gen_random_uuid(), organization_id, branch_id, id, $2, 15, DATE '2026-04-01', 'Import', created_by_user_id
                FROM price_lists WHERE id = $1
                """, owner);
            cmd.Parameters.AddWithValue(first);
            cmd.Parameters.AddWithValue(presentation);
            cmd.ExecuteNonQuery();
        }

        var response = await admin.PostAsJsonAsync("/catalog/copy", new { sourceBranchId = rutaCincuentaYUnoId, targetBranchId = centroId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SetBranchHeader(admin, centroId);
        var lists = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/pricing/price-lists");
        Assert.Equal(1, lists.GetArrayLength());
        Assert.Equal("First list", lists[0].GetProperty("name").GetString());
    }
}
