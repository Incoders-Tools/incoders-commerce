using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T6: the staff admin endpoints that act on an existing user (reset password, replace
/// roles) apply the same target rules as revoke/restore — the target never holds permissions beyond the caller's and
/// stays inside the caller's branch scope — for the browser cookie and the device operator alike. A reset is audited
/// without password material, and a staff user created from a terminal lands in the terminal's branch.
/// </summary>
[Collection("Postgres")]
public sealed class StaffTargetRulesTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private const string OperatorHeader = "X-Operator-Id";
    private const string ExceedsCaller = """{"error":"permissions-exceed-caller"}""";
    private const string BranchNotInScope = """{"error":"branch-not-in-scope"}""";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public StaffTargetRulesTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (_postgresAvailable) ApplyMigrations();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrations()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static object? OwnerScalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd.ExecuteScalar();
    }

    private static void OwnerExec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private sealed record Tenant(Guid OrganizationId, Guid BranchId, Guid AdminId, string AdminEmail);

    /// <summary>
    /// The organization owner (business admin), a branch manager (<c>ManageUsers | OperatePos</c>) and a cashier in
    /// the owner's branch, and a cashier in another branch.
    /// </summary>
    private sealed record World(Tenant Tenant, HttpClient Owner, Guid ManagerId, string ManagerEmail, Guid CashierId, Guid OtherBranchId, Guid OtherCashierId);

    private async Task<Tenant> BootstrapAsync(string prefix)
    {
        var email = Unique(prefix);
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        return new Tenant(organizationId, body.BranchId, body.UserId, email);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
    });

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = NewClient();
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<string> PairDeviceAsync(Tenant tenant)
    {
        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>().IssueAsync(
            new CloudTenantScope(tenant.OrganizationId), Guid.NewGuid(), tenant.BranchId, tenant.AdminId, CancellationToken.None);
        return issued.PlaintextToken;
    }

    private HttpClient DeviceClient(string deviceToken, Guid operatorId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        client.DefaultRequestHeaders.Add(OperatorHeader, operatorId.ToString());
        return client;
    }

    private static async Task<(Guid UserId, string Email)> CreateStaffAsync(HttpClient admin, string role, Guid branchId)
    {
        var email = Unique("str-staff");
        var response = await admin.PostAsJsonAsync("/account/users", new CreateUserRequest(email, Password, [role], [branchId]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await response.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId, email);
    }

    private static async Task<Guid> CreateBranchAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/account/branches", new CreateBranchRequest("Otra " + Guid.NewGuid().ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateBranchResponse>())!.BranchId;
    }

    private async Task<World> NewWorldAsync(string prefix)
    {
        var tenant = await BootstrapAsync(prefix);
        var owner = await SignInAsync(tenant.AdminEmail);

        var (managerId, managerEmail) = await CreateStaffAsync(owner, RoleCatalog.Cashier, tenant.BranchId);
        OwnerExec("UPDATE users SET roles = $1::jsonb WHERE id = $2",
            JsonSerializer.Serialize(new[] { new { name = "manager", permissions = (int)(Permission.ManageUsers | Permission.OperatePos) } }),
            managerId);

        var (cashierId, _) = await CreateStaffAsync(owner, RoleCatalog.Cashier, tenant.BranchId);

        // The owner cannot assign a branch it lacks, so the other branch's cashier is moved there directly.
        var otherBranchId = await CreateBranchAsync(owner);
        var (otherCashierId, _) = await CreateStaffAsync(owner, RoleCatalog.Cashier, tenant.BranchId);
        OwnerExec("UPDATE users SET branch_scope = ARRAY[$1]::uuid[] WHERE id = $2", otherBranchId, otherCashierId);

        return new World(tenant, owner, managerId, managerEmail, cashierId, otherBranchId, otherCashierId);
    }

    private async Task<HttpClient> ManagerClientAsync(World w, bool device) =>
        device ? DeviceClient(await PairDeviceAsync(w.Tenant), w.ManagerId) : await SignInAsync(w.ManagerEmail);

    private static string PasswordHash(Guid userId) => (string)OwnerScalar("SELECT password_hash FROM users WHERE id = $1", userId)!;

    private static string RolesJson(Guid userId) => (string)OwnerScalar("SELECT roles::text FROM users WHERE id = $1", userId)!;

    private static long ResetAuditCount(Guid userId) =>
        (long)OwnerScalar("SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'user.password.reset'", userId)!;

    // ------------------------------------------------------------------
    // Reset password
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_CannotResetTheOwnersPassword(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-reset-owner");
        var manager = await ManagerClientAsync(w, device);
        var before = PasswordHash(w.Tenant.AdminId);

        var response = await manager.PostAsJsonAsync(
            $"/account/users/{w.Tenant.AdminId}/reset-password", new AdminResetPasswordRequest("taken-over-123"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ExceedsCaller, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, PasswordHash(w.Tenant.AdminId));
        Assert.Equal(0L, ResetAuditCount(w.Tenant.AdminId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_CannotResetThePasswordOfAUserOfAnotherBranch(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-reset-branch");
        var manager = await ManagerClientAsync(w, device);
        var before = PasswordHash(w.OtherCashierId);

        var response = await manager.PostAsJsonAsync(
            $"/account/users/{w.OtherCashierId}/reset-password", new AdminResetPasswordRequest("taken-over-123"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(BranchNotInScope, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, PasswordHash(w.OtherCashierId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_ResetsThePasswordOfAUserOfTheirBranch_AndTheResetIsAudited(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-reset-ok");
        var manager = await ManagerClientAsync(w, device);
        var before = PasswordHash(w.CashierId);

        var response = await manager.PostAsJsonAsync(
            $"/account/users/{w.CashierId}/reset-password", new AdminResetPasswordRequest("brand-new-password-1"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(before, PasswordHash(w.CashierId));
        Assert.Equal(1L, ResetAuditCount(w.CashierId));
        Assert.Equal(w.ManagerId, (Guid)OwnerScalar(
            "SELECT actor_id FROM audit_log WHERE entity_id = $1 AND action = 'user.password.reset'", w.CashierId)!);
        Assert.Equal("org-user", (string)OwnerScalar(
            "SELECT actor_kind FROM audit_log WHERE entity_id = $1 AND action = 'user.password.reset'", w.CashierId)!);
        Assert.Equal(w.Tenant.OrganizationId, (Guid)OwnerScalar(
            "SELECT organization_id FROM audit_log WHERE entity_id = $1 AND action = 'user.password.reset'", w.CashierId)!);
        // No password material (neither the new password nor any hash) is written to the audit row.
        Assert.Equal(0L, (long)OwnerScalar(
            "SELECT count(*) FROM audit_log WHERE entity_id = $1 AND action = 'user.password.reset' AND (old_value IS NOT NULL OR new_value IS NOT NULL)",
            w.CashierId)!);

        // The new password works.
        var cashierEmail = (string)OwnerScalar("SELECT email FROM users WHERE id = $1", w.CashierId)!;
        var signIn = await NewClient().PostAsJsonAsync("/account/sign-in", new SignInRequest(cashierEmail, "brand-new-password-1"));
        Assert.True(signIn.IsSuccessStatusCode);
    }

    [Fact]
    public async Task SelfServiceRenewal_IsUnchanged_AndWritesNoResetAudit()
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-renew");
        var manager = await SignInAsync(w.ManagerEmail);

        var response = await manager.PostAsJsonAsync("/account/renew-password", new RenewPasswordRequest(Password, "renewed-password-1"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, ResetAuditCount(w.ManagerId));
    }

    // ------------------------------------------------------------------
    // Replace roles
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_CannotReplaceTheOwnersRoles(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-roles-owner");
        var manager = await ManagerClientAsync(w, device);
        var before = RolesJson(w.Tenant.AdminId);

        var response = await manager.PutAsJsonAsync(
            $"/account/users/{w.Tenant.AdminId}/roles", new AssignRolesRequest([RoleCatalog.Cashier]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ExceedsCaller, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, RolesJson(w.Tenant.AdminId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_CannotReplaceTheRolesOfAUserOfAnotherBranch(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-roles-branch");
        var manager = await ManagerClientAsync(w, device);
        var before = RolesJson(w.OtherCashierId);

        var response = await manager.PutAsJsonAsync(
            $"/account/users/{w.OtherCashierId}/roles", new AssignRolesRequest([]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(BranchNotInScope, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, RolesJson(w.OtherCashierId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABranchManager_ReplacesTheRolesOfAUserOfTheirBranch(bool device)
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-roles-ok");
        var manager = await ManagerClientAsync(w, device);

        var response = await manager.PutAsJsonAsync($"/account/users/{w.CashierId}/roles", new AssignRolesRequest([]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("[]", RolesJson(w.CashierId));
    }

    // ------------------------------------------------------------------
    // Staff created from a terminal
    // ------------------------------------------------------------------

    private static Guid[] BranchScope(Guid userId) => (Guid[])OwnerScalar("SELECT branch_scope FROM users WHERE id = $1", userId)!;

    [Fact]
    public async Task StaffCreatedFromATerminal_WithoutBranches_LandsInTheTerminalsBranch()
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-create-default");
        var device = DeviceClient(await PairDeviceAsync(w.Tenant), w.Tenant.AdminId);

        var response = await device.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("str-new"), Password, [RoleCatalog.Cashier], []));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId;
        Assert.Equal([w.Tenant.BranchId], BranchScope(userId));
    }

    [Fact]
    public async Task StaffCreatedFromATerminal_NamingTheTerminalsBranch_LandsThere()
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-create-same");
        var device = DeviceClient(await PairDeviceAsync(w.Tenant), w.Tenant.AdminId);

        var response = await device.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("str-new"), Password, [RoleCatalog.Cashier], [w.Tenant.BranchId, w.Tenant.BranchId]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var userId = (await response.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId;
        Assert.Equal([w.Tenant.BranchId], BranchScope(userId));
    }

    [Fact]
    public async Task StaffCreatedFromATerminal_NamingAnotherBranch_IsRefused_EvenWhenTheOperatorHoldsIt()
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-create-other");
        // The owner holds both branches, so only the terminal rule can refuse the other one.
        OwnerExec("UPDATE users SET branch_scope = ARRAY[$1, $2]::uuid[] WHERE id = $3", w.Tenant.BranchId, w.OtherBranchId, w.Tenant.AdminId);
        var device = DeviceClient(await PairDeviceAsync(w.Tenant), w.Tenant.AdminId);
        var users = (long)OwnerScalar("SELECT count(*) FROM users WHERE organization_id = $1", w.Tenant.OrganizationId)!;

        foreach (var branchIds in new[] { new[] { w.OtherBranchId }, new[] { w.Tenant.BranchId, w.OtherBranchId } })
        {
            var response = await device.PostAsJsonAsync("/account/users",
                new CreateUserRequest(Unique("str-new"), Password, [RoleCatalog.Cashier], branchIds));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(problem.GetProperty("errors").TryGetProperty("branchIds", out _));
        }

        Assert.Equal(users, (long)OwnerScalar("SELECT count(*) FROM users WHERE organization_id = $1", w.Tenant.OrganizationId)!);
    }

    [Fact]
    public async Task StaffCreatedFromTheBrowser_KeepsTheBranchAssignmentRules()
    {
        if (!_postgresAvailable) return;
        var w = await NewWorldAsync("str-create-cookie");

        var empty = await w.Owner.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("str-new"), Password, [RoleCatalog.Cashier], []));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("branch-required", await empty.Content.ReadAsStringAsync());

        OwnerExec("UPDATE users SET branch_scope = ARRAY[$1, $2]::uuid[] WHERE id = $3", w.Tenant.BranchId, w.OtherBranchId, w.Tenant.AdminId);
        var other = await w.Owner.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("str-new"), Password, [RoleCatalog.Cashier], [w.OtherBranchId]));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        var userId = (await other.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId;
        Assert.Equal([w.OtherBranchId], BranchScope(userId));
    }
}
