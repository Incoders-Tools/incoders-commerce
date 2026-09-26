using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Catalog;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-user-credentials task 3.6: a rename request whose body
/// carries forged actor fields (name kept for history/clarity even though
/// the current <c>RenameProductRequest</c> no longer HAS those fields — the
/// point of this test is that even sending them as extraneous JSON has zero
/// effect) is authorized using ONLY the store-loaded actor's real
/// roles/branch scope, never anything from the request body.
///
/// Updated for commerce-pricing-engine Work Unit 1: the rename endpoint now
/// authorizes over a REAL persisted product (looked up by id), not one
/// constructed from the request body — a product must be created first via
/// <c>POST /catalog/products</c>, mirroring the store-backed contract the
/// rest of this change depends on (identification codes, price entries).
/// </summary>
[Collection("Postgres")]
public sealed class CatalogEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogEndpointTests(WebApplicationFactory<Program> factory)
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

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "Commerce.sln")))
        {
            repoRoot = repoRoot.Parent;
        }
        if (repoRoot is null)
        {
            throw new InvalidOperationException("Could not locate repo root.");
        }

        var initSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var organizationsSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(organizationsSql, owner)) cmd.ExecuteNonQuery();

        // B7 U4: 0009's presentations_org_code_uk is ORG-scoped, but a
        // prior test method in this SAME class (and physical database —
        // commerce_test is shared/accumulating) may have left presentations
        // with the SAME identification_code in TWO DIFFERENT branches of
        // one org, which 0016 legitimately allows. Truncating before 0009
        // re-runs means that leftover data never has to satisfy 0009's
        // (temporarily reinstated, since 0016 already dropped it here)
        // stricter org-only index while it is being recreated.
        using (var truncateCatalogCmd = new NpgsqlCommand(
            "TRUNCATE TABLE presentations, products CASCADE", owner))
        {
            try { truncateCatalogCmd.ExecuteNonQuery(); } catch (PostgresException) { /* first run: tables don't exist yet */ }
        }

        // commerce-pricing-engine Work Unit 1: products/presentations now
        // back the rename endpoint for real, so this test's organization/
        // product rows must exist against real FKs.
        var catalogAndPricingSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0009_catalog_and_pricing.sql"));
        using (var cmd = new NpgsqlCommand(catalogAndPricingSql, owner)) cmd.ExecuteNonQuery();

        // B7 U4: products/presentations are branch-owned — every `/catalog/*`
        // route now requires a selected branch.
        var branchOwnershipSql = File.ReadAllText(Path.Combine(repoRoot.FullName, "deploy", "db", "migrations", "0016_catalog_branch_ownership.sql"));
        using (var cmd = new NpgsqlCommand(branchOwnershipSql, owner)) cmd.ExecuteNonQuery();

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE presentations, products, user_directory, users, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Products carry a real FK to `organizations` (0009) — a product
    /// created for an organization id with no matching row would violate
    /// the FK, so the fixture must seed the organization first.
    /// </summary>
    private async Task SeedOrganizationAsync(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// B7 U4: `/catalog/*` requires a REAL branch row (composite FK,
    /// `BranchSelectionRequirement`/`TenantScopeEndpointFilter` validation),
    /// not merely an entry in a user's `BranchScope` array.
    /// </summary>
    private async Task SeedBranchAsync(Guid organizationId, Guid branchId, string name = "Main")
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, $3)", owner);
        cmd.Parameters.AddWithValue(branchId);
        cmd.Parameters.AddWithValue(organizationId);
        cmd.Parameters.AddWithValue(name);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> CreateProductAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/catalog/products", new
        {
            name = "Seed Product",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Test-only endpoint-free sign-in helper: creates a real user with the
    /// given real permission set and returns an authenticated HttpClient
    /// carrying the resulting cookie — proving the rename endpoint's
    /// authorization decision is driven ONLY by what is persisted for this
    /// user, not by anything the caller can inject into the rename request.
    /// </summary>
    private async Task<(HttpClient client, Guid organizationId, Guid branchId)> SignedInClientAsync(
        Permission permissions, Guid[]? branchScope = null, Guid? organizationId = null)
    {
        var isNewOrganization = organizationId is null;
        organizationId ??= Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        if (isNewOrganization)
        {
            await SeedOrganizationAsync(organizationId.Value);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var newUser = new NewUserAccount(
                userId, $"{userId}@example.com", "unused-hash", branchScope ?? new[] { branchId }, new[] { new RoleDto("test-role", permissions) });
            var tenantScope = new CloudTenantScope(organizationId.Value);

            if (isNewOrganization)
            {
                // TryCreateAsync is the GENESIS path: it only succeeds when
                // the organization has ZERO users. Reusing an organization
                // (e.g. a second actor in the SAME org) must go through the
                // ordinary staff-create path instead.
                var created = await store.TryCreateAsync(tenantScope, newUser, CancellationToken.None);
                Assert.True(created);
            }
            else
            {
                var outcome = await store.CreateStaffUserAsync(
                    tenantScope, newUser,
                    new UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId.Value, "user", userId, "user.created", null, null),
                    CancellationToken.None);
                Assert.Equal(CreateStaffUserOutcome.Created, outcome);
            }
        }

        // A unique-per-branch name: this org may already hold a branch (a
        // second actor signed into the SAME organization, e.g.
        // `Rename_WhenStoredActorLacksPermission_...`), and
        // `branches_org_name_unique` rejects a second "Main".
        await SeedBranchAsync(organizationId.Value, branchId, $"Branch {branchId:N}");

        var client = await SignInViaTestEndpointAsync(organizationId.Value, userId);
        // Every call this client makes selects `branchId` — matches this
        // caller's own BranchScope (or the default single-element scope
        // above), so `TenantScopeEndpointFilter` honors it for every
        // `/catalog/*` request without each test having to add it by hand.
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branchId.ToString());
        return (client, organizationId.Value, branchId);
    }

    private async Task<HttpClient> SignInViaTestEndpointAsync(Guid organizationId, Guid userId)
    {
        // Use the real cookie-issuing mechanism through a minimal local
        // sign-in shortcut: bootstrap a token-less path is unnecessary here
        // because AccountEndpoints only issues cookies after real password
        // verification. Instead, seed a known password and sign in for real.
        using (var scope = _factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            // The user was already created by SignedInClientAsync with an
            // "unused-hash" placeholder; overwrite with a real hash for a
            // known password so we can sign in for real over HTTP.
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

            // BaseAddress MUST be https:// — Program.cs sets
            // CookieSecurePolicy.Always, so the auth cookie is only set/sent
            // on a request TestServer treats as secure.
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
                BaseAddress = new Uri("https://localhost"),
            });
            var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, "test-password"));
            Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
            return client;
        }
    }

    [Fact]
    public async Task Rename_WithForgedActorFieldsInBody_IsIgnored_StoredActorPermissionsGovern_Allowed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, branchId) = await SignedInClientAsync(Permission.ManageCatalog);
        var productId = await CreateProductAsync(client);

        var forgedBody = JsonSerializer.Serialize(new
        {
            // Extraneous forged fields a naive deserializer might have honored
            // under the OLD RenameProductRequest shape — must have zero effect.
            actorId = Guid.NewGuid(),
            actorBranchScope = new[] { Guid.NewGuid() },
            actorRoles = new[] { new { name = "super-admin", permissions = 15 } },
            targetBranchId = branchId,
            currentName = "Old Name",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
            newName = "New Name",
            isOffline = false,
            correlationId = Guid.NewGuid(),
        });

        var response = await client.PostAsync(
            $"/catalog/products/{productId}/rename",
            new StringContent(forgedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rename_WhenStoredActorLacksPermission_Returns403_EvenWithForgedRoleClaim()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        // A properly-authorized creator seeds the product in the org first —
        // this test asserts the DENIAL path, not product existence, so the
        // product must be real for the same reason the ALLOW test's is.
        var (creatorClient, organizationId, _) = await SignedInClientAsync(Permission.ManageCatalog);
        var productId = await CreateProductAsync(creatorClient);

        var (client, _, branchId) = await SignedInClientAsync(Permission.None, organizationId: organizationId);

        var forgedBody = JsonSerializer.Serialize(new
        {
            actorId = Guid.NewGuid(),
            actorRoles = new[] { new { name = "super-admin", permissions = 15 } },
            targetBranchId = branchId,
            currentName = "Old Name",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
            newName = "New Name",
            isOffline = false,
            correlationId = Guid.NewGuid(),
        });

        var response = await client.PostAsync(
            $"/catalog/products/{productId}/rename",
            new StringContent(forgedBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- B7 U4: branch-owned catalog -----------------------------------------

    [Fact]
    public async Task ListProducts_WithNoBranchHeader_Returns400_BranchSelectionRequired()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, _, _) = await SignedInClientAsync(Permission.ManageCatalog);
        client.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);

        var response = await client.GetAsync("/catalog/products");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("branch-selection-required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateProduct_WithOutOfScopeBranchHeader_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, _) = await SignedInClientAsync(Permission.ManageCatalog);
        var otherBranchId = Guid.NewGuid();
        await SeedBranchAsync(organizationId, otherBranchId, "Centro");

        client.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, otherBranchId.ToString());

        var response = await client.PostAsJsonAsync("/catalog/products", new
        {
            name = "Should Not Be Created",
            categoryId = Guid.NewGuid(),
            defaultUnitId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// catalog-item-identification "Branch-Owned Catalog": products/
    /// presentations created with one branch selected are invisible to a
    /// caller who selects a DIFFERENT branch of the SAME organization, even
    /// for a caller who could act on both (mirrors "Ruta 51"/"Centro" of one
    /// Vaca Verde-shaped organization).
    /// </summary>
    [Fact]
    public async Task Products_CreatedUnderOneBranch_AreInvisible_WhenADifferentBranchOfTheSameOrgIsSelected()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, rutaCincuentaYUnoId) = await SignedInClientAsync(
            Permission.ManageCatalog, branchScope: null, organizationId: null);
        var centroId = Guid.NewGuid();
        await SeedBranchAsync(organizationId, centroId, "Centro");
        // This user may act on BOTH branches — isolation must still hold.
        using (var scope = _factory.Services.CreateScope())
        {
            using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
            owner.Open();
            using var widen = new NpgsqlCommand(
                "UPDATE users SET branch_scope = branch_scope || $1::uuid WHERE organization_id = $2", owner);
            widen.Parameters.AddWithValue(centroId);
            widen.Parameters.AddWithValue(organizationId);
            widen.ExecuteNonQuery();
        }

        var productId = await CreateProductAsync(client); // selected branch: Ruta 51

        client.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, centroId.ToString());

        var fromCentro = await client.GetAsync($"/catalog/products/{productId}");
        Assert.Equal(HttpStatusCode.NotFound, fromCentro.StatusCode);

        var listFromCentro = await client.GetAsync("/catalog/products");
        var products = await listFromCentro.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(products.EnumerateArray(), p => p.GetProperty("id").GetGuid() == productId);

        client.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, rutaCincuentaYUnoId.ToString());
        var fromRuta51 = await client.GetAsync($"/catalog/products/{productId}");
        Assert.Equal(HttpStatusCode.OK, fromRuta51.StatusCode);
    }

    /// <summary>
    /// catalog-item-identification "Branch-Owned Catalog" scenario "Same
    /// barcode in two Vaca Verde branches": the same identification code is
    /// accepted in two different branches of one organization.
    /// </summary>
    [Fact]
    public async Task CreatePresentation_SameIdentificationCode_AcceptedInTwoBranchesOfSameOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (client, organizationId, rutaCincuentaYUnoId) = await SignedInClientAsync(Permission.ManageCatalog);
        var centroId = Guid.NewGuid();
        await SeedBranchAsync(organizationId, centroId, "Centro");
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var widen = new NpgsqlCommand(
                "UPDATE users SET branch_scope = branch_scope || $1::uuid WHERE organization_id = $2", owner);
            widen.Parameters.AddWithValue(centroId);
            widen.Parameters.AddWithValue(organizationId);
            widen.ExecuteNonQuery();
        }

        var productId = await CreateProductAsync(client); // Ruta 51
        var firstResponse = await client.PostAsJsonAsync("/catalog/presentations", new
        {
            productId,
            name = "1kg bag",
            quantityBehavior = QuantityBehavior.FixedQuantity,
            unitId = Guid.NewGuid(),
            identificationCode = "7791234567890",
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        // Same code again, in Ruta 51: rejected.
        var duplicateResponse = await client.PostAsJsonAsync("/catalog/presentations", new
        {
            productId,
            name = "1kg bag (dup)",
            quantityBehavior = QuantityBehavior.FixedQuantity,
            unitId = Guid.NewGuid(),
            identificationCode = "7791234567890",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        // Switch to Centro: a product must exist there first (products are
        // branch-owned too), then the SAME code is accepted.
        client.DefaultRequestHeaders.Remove(TenantScopeEndpointFilter.BranchSelectorHeader);
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, centroId.ToString());
        var centroProductId = await CreateProductAsync(client);

        var centroResponse = await client.PostAsJsonAsync("/catalog/presentations", new
        {
            productId = centroProductId,
            name = "1kg bag",
            quantityBehavior = QuantityBehavior.FixedQuantity,
            unitId = Guid.NewGuid(),
            identificationCode = "7791234567890",
        });
        Assert.Equal(HttpStatusCode.Created, centroResponse.StatusCode);
        _ = rutaCincuentaYUnoId;
    }
}
