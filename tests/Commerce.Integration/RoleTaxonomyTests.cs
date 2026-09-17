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

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-role-taxonomy Phase 3 (tasks 3.1-3.17): `POST
/// /account/users`, `PUT /account/users/{userId}/roles`, the
/// <see cref="RoleGrantPolicy"/> grant cap enforced at the endpoint boundary,
/// and the in-transaction audit row for both actions (design.md "Data
/// Flow"). Against a live Postgres instance (`deploy/dev/compose.yaml`); if
/// unreachable, each test reports the gap and returns without asserting
/// pass/fail, matching the repo's existing fixture convention.
/// </summary>
[Collection("Postgres")]
public sealed class RoleTaxonomyTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public RoleTaxonomyTests(WebApplicationFactory<Program> factory)
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
            throw new InvalidOperationException("Could not locate repo root (Commerce.sln).");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();
        foreach (var fileName in new[]
                 {
                     "0001_init_rls.sql",
                     "0002_users.sql",
                     "0003_organizations_branches.sql",
                     "0004_device_credentials.sql",
                     "0005_password_recovery.sql",
                     "0006_role_taxonomy.sql",
                     "0007_platform_administration.sql",
                 })
        {
            var sql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", fileName))
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE audit_log RESTART IDENTITY; " +
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations, platform_admins CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid AdminUserId)> BootstrapOrgAsync(
        HttpClient client, string adminEmail, string adminPassword)
    {
        var organizationId = Guid.NewGuid();
        var registry = _factory.Services.GetRequiredService<BootstrapTokenRegistry>();
        var token = registry.Issue(organizationId);

        var response = await client.PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", adminEmail, adminPassword));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();

        return (organizationId, body!.BranchId, body.UserId);
    }

    /// <summary>
    /// Seeds a SECOND user directly in an already-bootstrapped organization
    /// with an arbitrary role name/permission set — mirrors
    /// `AccountEndpointTests.SeedUserAsync`'s raw-insert fallback, since
    /// `PostgresUserAccountStore.TryCreateAsync` only allows the very first
    /// (bootstrap) user per organization.
    /// </summary>
    private static void SeedSecondUser(
        Guid organizationId, Guid userId, string email, string passwordHash, string roleName, Permission permissions, Guid[] branchScope)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var rolesJson = JsonSerializer.Serialize(
            new[] { new RoleDto(roleName, permissions) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using (var insertUserCmd = new NpgsqlCommand(
            """
            INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb)
            """, owner))
        {
            insertUserCmd.Parameters.AddWithValue(userId);
            insertUserCmd.Parameters.AddWithValue(organizationId);
            insertUserCmd.Parameters.AddWithValue(normalizedEmail);
            insertUserCmd.Parameters.AddWithValue(passwordHash);
            insertUserCmd.Parameters.AddWithValue(branchScope);
            insertUserCmd.Parameters.AddWithValue(rolesJson);
            insertUserCmd.ExecuteNonQuery();
        }
        using (var insertDirectoryCmd = new NpgsqlCommand(
            "INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1, $2, $3)", owner))
        {
            insertDirectoryCmd.Parameters.AddWithValue(normalizedEmail);
            insertDirectoryCmd.Parameters.AddWithValue(organizationId);
            insertDirectoryCmd.Parameters.AddWithValue(userId);
            insertDirectoryCmd.ExecuteNonQuery();
        }
    }

    private string HashPassword(Guid userId, Guid organizationId, string plaintext)
    {
        using var scope = _factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
        return hasher.HashPassword(new UserAccount(userId, organizationId, [], []), plaintext);
    }

    private static int CountUsersByEmail(string email)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM users WHERE email = $1", owner);
        cmd.Parameters.AddWithValue(email.Trim().ToLowerInvariant());
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private static (string ActorKind, Guid ActorId, Guid? OrganizationId, string Action, string? OldValue, string? NewValue)?
        FindAuditRow(string entityType, Guid entityId, string action)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_kind, actor_id, organization_id, action, old_value::text, new_value::text " +
            "FROM audit_log WHERE entity_type = $1 AND entity_id = $2 AND action = $3", owner);
        cmd.Parameters.AddWithValue(entityType);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return (
            reader.GetString(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static long CountAuditRows(string entityType, Guid entityId, string action)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE entity_type = $1 AND entity_id = $2 AND action = $3", owner);
        cmd.Parameters.AddWithValue(entityType);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(action);
        return (long)cmd.ExecuteScalar()!;
    }

    private static string GetRolesJson(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT roles::text FROM users WHERE id = $1", owner);
        cmd.Parameters.AddWithValue(userId);
        return (string)cmd.ExecuteScalar()!;
    }

    private static WebApplicationFactoryClientOptions CookieClientOptions() => new()
    {
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    };

    // --- 3.1 / 3.9-3.10: create ---------------------------------------------

    [Fact]
    public async Task CreateUser_BusinessAdminCreatesSellerInSameOrg_Returns201_WithExactCatalogPermissions()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, _) = await BootstrapOrgAsync(client, "3-1-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-1-admin@example.com", "admin-password"));

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-1-seller@example.com", "seller-password", [RoleCatalog.Seller], [branchId]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateUserResponse>();
        Assert.NotEqual(Guid.Empty, body!.UserId);

        var rolesJson = GetRolesJson(body.UserId);
        Assert.Contains("\"seller\"", rolesJson);

        var sellerClient = _factory.CreateClient();
        var sellerSignIn = await sellerClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("3-1-seller@example.com", "seller-password"));
        Assert.Equal(HttpStatusCode.OK, sellerSignIn.StatusCode);
    }

    [Fact]
    public async Task CreateUser_UnknownRoleName_Returns400_AndNoUserPersisted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (_, branchId, _) = await BootstrapOrgAsync(client, "3-2-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-2-admin@example.com", "admin-password"));

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-2-unknown@example.com", "password", ["not-a-real-role"], [branchId]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, CountUsersByEmail("3-2-unknown@example.com"));
    }

    [Fact]
    public async Task CreateUser_TranslatedRoleName_Returns400_NeverMappedToSeller()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (_, branchId, _) = await BootstrapOrgAsync(client, "3-3-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-3-admin@example.com", "admin-password"));

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-3-vendedor@example.com", "password", ["Vendedor"], [branchId]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, CountUsersByEmail("3-3-vendedor@example.com"));
    }

    [Fact]
    public async Task CreateUser_ProviderRole_GrantsNoCapability()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (_, branchId, _) = await BootstrapOrgAsync(client, "3-4-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-4-admin@example.com", "admin-password"));

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-4-provider@example.com", "password", [RoleCatalog.Provider], [branchId]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateUserResponse>();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        Guid actualOrgId;
        using (var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString))
        {
            owner.Open();
            using var cmd = new NpgsqlCommand("SELECT organization_id FROM users WHERE id = $1", owner);
            cmd.Parameters.AddWithValue(body!.UserId);
            actualOrgId = (Guid)cmd.ExecuteScalar()!;
        }

        var actor = await store.LoadActorAsync(new CloudTenantScope(actualOrgId), body!.UserId, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.Equal(Permission.None, actor!.EffectivePermissions);
    }

    [Fact]
    public async Task CreateUser_CrossOrganizationBranch_Returns400_AndNoUserPersisted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var clientA = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(clientA, "3-5-admin-a@example.com", "admin-password");
        await clientA.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-5-admin-a@example.com", "admin-password"));

        var clientB = _factory.CreateClient();
        var (_, branchIdB, _) = await BootstrapOrgAsync(clientB, "3-5-admin-b@example.com", "admin-password-b");

        var response = await clientA.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-5-crossbranch@example.com", "password", [RoleCatalog.Seller], [branchIdB]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, CountUsersByEmail("3-5-crossbranch@example.com"));
    }

    [Fact]
    public async Task CreateUser_GrantExceedsCallerPermissions_Returns403_AndNoUserPersisted()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, _) = await BootstrapOrgAsync(client, "3-6-admin@example.com", "admin-password");

        // Second user: holds ManageUsers + ViewSales but NOT ManageCatalog or
        // ManageBranchSettings — a proper subset of business-admin.
        var limitedId = Guid.NewGuid();
        var hash = HashPassword(limitedId, organizationId, "limited-password");
        SeedSecondUser(
            organizationId, limitedId, "3-6-limited@example.com", hash, "seller-with-manage-users",
            Permission.ManageUsers | Permission.ViewSales, [branchId]);

        var limitedClient = _factory.CreateClient(CookieClientOptions());
        var signIn = await limitedClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("3-6-limited@example.com", "limited-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // business-admin's permission set includes ManageCatalog, which the
        // caller does not hold.
        var response = await limitedClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-6-escalated@example.com", "password", [RoleCatalog.BusinessAdmin], [branchId]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, CountUsersByEmail("3-6-escalated@example.com"));
    }

    [Fact]
    public async Task CreateUser_CallerWithoutManageUsers_Returns403()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, _) = await BootstrapOrgAsync(client, "3-7-admin@example.com", "admin-password");

        var noPermId = Guid.NewGuid();
        var hash = HashPassword(noPermId, organizationId, "noperm-password");
        SeedSecondUser(organizationId, noPermId, "3-7-noperm@example.com", hash, "seller", Permission.ViewSales, [branchId]);

        var noPermClient = _factory.CreateClient(CookieClientOptions());
        var signIn = await noPermClient.PostAsJsonAsync(
            "/account/sign-in", new SignInRequest("3-7-noperm@example.com", "noperm-password"));
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var response = await noPermClient.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-7-target@example.com", "password", [RoleCatalog.Seller], [branchId]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_Success_WritesExactlyOneAuditRow_InsideTheSameAction()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, adminUserId) = await BootstrapOrgAsync(client, "3-8-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-8-admin@example.com", "admin-password"));

        var response = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-8-seller@example.com", "password", [RoleCatalog.Seller], [branchId]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateUserResponse>();

        Assert.Equal(1, CountAuditRows("user", body!.UserId, "user.created"));
        var row = FindAuditRow("user", body.UserId, "user.created");
        Assert.NotNull(row);
        Assert.Equal("org-user", row!.Value.ActorKind);
        Assert.Equal(adminUserId, row.Value.ActorId);
        Assert.Equal(organizationId, row.Value.OrganizationId);
        Assert.Contains("seller", row.Value.NewValue);
    }

    // --- 3.11-3.14 / 3.16-3.17: roles ----------------------------------------

    [Fact]
    public async Task AssignRoles_PersistsChange_AndEffectivePermissionsReflectIt()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, _) = await BootstrapOrgAsync(client, "3-11-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-11-admin@example.com", "admin-password"));

        var create = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-11-target@example.com", "password", [RoleCatalog.Seller], [branchId]));
        var created = await create.Content.ReadFromJsonAsync<CreateUserResponse>();

        var response = await client.PutAsJsonAsync(
            $"/account/users/{created!.UserId}/roles", new AssignRolesRequest([RoleCatalog.Provider]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var actor = await store.LoadActorAsync(new CloudTenantScope(organizationId), created.UserId, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.Equal(Permission.None, actor!.EffectivePermissions);
        Assert.Equal(RoleCatalog.Provider, actor.Roles.Single().Name);
    }

    [Fact]
    public async Task AssignRoles_CrossOrganizationTarget_Returns404_IdenticalToNonexistentId()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var clientA = _factory.CreateClient(CookieClientOptions());
        await BootstrapOrgAsync(clientA, "3-12-admin-a@example.com", "admin-password");
        await clientA.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-12-admin-a@example.com", "admin-password"));

        var clientB = _factory.CreateClient();
        var (_, _, crossOrgUserId) = await BootstrapOrgAsync(clientB, "3-12-admin-b@example.com", "admin-password-b");

        var crossOrgResponse = await clientA.PutAsJsonAsync(
            $"/account/users/{crossOrgUserId}/roles", new AssignRolesRequest([RoleCatalog.Seller]));
        var nonexistentResponse = await clientA.PutAsJsonAsync(
            $"/account/users/{Guid.NewGuid()}/roles", new AssignRolesRequest([RoleCatalog.Seller]));

        Assert.Equal(HttpStatusCode.NotFound, crossOrgResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistentResponse.StatusCode);
    }

    [Fact]
    public async Task AssignRoles_PlatformAdminGrant_IsDenied_EvenForAnAllFlagsCaller()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        // The bootstrap admin already holds every flag
        // (ViewSales|ManageCatalog|ManageUsers|ManageBranchSettings).
        var client = _factory.CreateClient(CookieClientOptions());
        var (_, branchId, adminUserId) = await BootstrapOrgAsync(client, "3-13-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-13-admin@example.com", "admin-password"));

        var create = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-13-target@example.com", "password", [RoleCatalog.Seller], [branchId]));
        var created = await create.Content.ReadFromJsonAsync<CreateUserResponse>();

        var response = await client.PutAsJsonAsync(
            $"/account/users/{created!.UserId}/roles", new AssignRolesRequest([RoleCatalog.PlatformAdmin]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var rolesJson = GetRolesJson(created.UserId);
        Assert.DoesNotContain("platform-admin", rolesJson);
    }

    [Fact]
    public async Task AssignRoles_Success_AuditRow_RecordsOldAndNewRoleNames()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, adminUserId) = await BootstrapOrgAsync(client, "3-14-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-14-admin@example.com", "admin-password"));

        var create = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-14-target@example.com", "password", [RoleCatalog.Seller], [branchId]));
        var created = await create.Content.ReadFromJsonAsync<CreateUserResponse>();

        var response = await client.PutAsJsonAsync(
            $"/account/users/{created!.UserId}/roles", new AssignRolesRequest([RoleCatalog.Provider]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = FindAuditRow("user", created.UserId, "user.roles.assigned");
        Assert.NotNull(row);
        Assert.Equal("org-user", row!.Value.ActorKind);
        Assert.Equal(adminUserId, row.Value.ActorId);
        Assert.Equal(organizationId, row.Value.OrganizationId);
        Assert.Contains("seller", row.Value.OldValue);
        Assert.Contains("provider", row.Value.NewValue);
    }

    /// <summary>
    /// Forces the audit insert to violate `audit_log_append`'s RLS policy
    /// (mismatched `organization_id`, never set by
    /// <see cref="PostgresUserAccountStore.ReplaceRolesAsync"/> itself) —
    /// proving the preceding `UPDATE users` in the SAME transaction rolls
    /// back with it: no orphaned mutation, no orphaned audit row.
    /// </summary>
    [Fact]
    public async Task RolesUpdate_FailedAuditWrite_RollsBackTheMutation_NoOrphanedRow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient(CookieClientOptions());
        var (organizationId, branchId, _) = await BootstrapOrgAsync(client, "3-17-admin@example.com", "admin-password");
        await client.PostAsJsonAsync("/account/sign-in", new SignInRequest("3-17-admin@example.com", "admin-password"));
        var createHttp = await client.PostAsJsonAsync(
            "/account/users",
            new CreateUserRequest("3-17-target@example.com", "password", [RoleCatalog.Seller], [branchId]));
        var created = await createHttp.Content.ReadFromJsonAsync<CreateUserResponse>();
        var targetId = created!.UserId;
        var originalRolesJson = GetRolesJson(targetId);
        var otherOrganizationId = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            await using (var scopeCmd = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx))
            {
                scopeCmd.Parameters.AddWithValue(organizationId.ToString());
                await scopeCmd.ExecuteNonQueryAsync();
            }

            await using (var updateCmd = new NpgsqlCommand(
                "UPDATE users SET roles = $1::jsonb WHERE id = $2", connection, tx))
            {
                updateCmd.Parameters.AddWithValue("""[{"name":"provider","permissions":0}]""");
                updateCmd.Parameters.AddWithValue(targetId);
                await updateCmd.ExecuteNonQueryAsync();
            }

            // organization_id deliberately does NOT match the scoped
            // app.current_org_id -> audit_log_append's WITH CHECK rejects it.
            await Assert.ThrowsAsync<PostgresException>(() => AuditLogWriter.InsertAsync(
                connection, tx,
                new UserManagementAuditEntry(
                    "org-user", Guid.NewGuid(), otherOrganizationId, "user", targetId, "user.roles.assigned", null, "{}"),
                CancellationToken.None));

            await tx.RollbackAsync();
        }

        Assert.Equal(originalRolesJson, GetRolesJson(targetId));
        Assert.Equal(0, CountAuditRows("user", targetId, "user.roles.assigned"));
    }
}
