using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// B7 U1 — tenant-access-foundation spec "Selected Branch Scopes Every
/// Branch-Owned Staff Request", platform-administration spec "Sysadmin
/// Selects Any Branch Of The Selected Organization", and
/// organization-persistence spec "Selectable Branches In The Session".
/// Reuses <see cref="SystemAdminOrganizationScopeTests"/>'s fixture shape
/// (bootstrap + raw-SQL sysadmin promotion) and
/// <see cref="DeviceEndpointTests"/>'s device-credential issuance rather
/// than duplicating either.
/// </summary>
[Collection("Postgres")]
public sealed class BranchSelectionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string OrganizationSelectorHeader = "X-Organization-Id";
    private const string BranchSelectorHeader = "X-Branch-Id";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public BranchSelectionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndReset();
    }

    public void Dispose() => _factory.Dispose();

    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Commerce.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "deploy", "db", "migrations"), "*.sql").OrderBy(Path.GetFileName))
        {
            var sql = File.ReadAllText(file)
                .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password")
                .Replace("__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE audit_log, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email, string password)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// Mirrors the real invariant (B1: <c>PromoteToSystemAdminAsync</c> /
    /// provision-admin.ps1) that a sysadmin's own BranchScope is always
    /// empty — unlike production, this test bootstraps the sysadmin through
    /// the ordinary single-org bootstrap flow (which DOES assign a branch),
    /// so `branch_scope` must be cleared here too or "sysadmin with no
    /// selection" tests would see that org's branch leak through.
    /// </summary>
    private static void PromoteToSystemAdmin(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE id = $1", owner);
        flag.Parameters.AddWithValue(userId);
        flag.ExecuteNonQuery();
    }

    /// <summary>Adds a second branch to an organization WITHOUT granting any user's BranchScope access to it.</summary>
    private async Task<Guid> AddOutOfScopeBranchAsync(Guid organizationId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        var branchId = Guid.NewGuid();
        await orgStore.CreateBranchAsync(new CloudTenantScope(organizationId), new NewBranch(branchId, name), CancellationToken.None);
        return branchId;
    }

    private static HttpRequestMessage WithHeaders(HttpMethod method, string url, (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        return request;
    }

    // --- Staff (cookie) caller: X-Branch-Id validation -----------------------

    [Fact]
    public async Task StaffCaller_SelectingOwnScopeBranch_Succeeds()
    {
        if (!_postgresAvailable) return;
        var (_, branchId, userId) = await BootstrapAsync("branch-own-scope@example.com", "correct-password");
        var admin = await SignInAsync("branch-own-scope@example.com", "correct-password");

        var response = await admin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches", [(BranchSelectorHeader, branchId.ToString())]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = userId;
    }

    [Fact]
    public async Task StaffCaller_SelectingUnknownBranch_Returns403()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("branch-unknown@example.com", "correct-password");
        var admin = await SignInAsync("branch-unknown@example.com", "correct-password");

        var response = await admin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches", [(BranchSelectorHeader, Guid.NewGuid().ToString())]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StaffCaller_SelectingOtherOrganizationsBranch_Returns403_SameStatusAsUnknown()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("branch-org-a@example.com", "correct-password");
        var (otherOrgId, otherBranchId, _) = await BootstrapAsync("branch-org-b@example.com", "correct-password");
        var adminA = await SignInAsync("branch-org-a@example.com", "correct-password");

        var response = await adminA.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches", [(BranchSelectorHeader, otherBranchId.ToString())]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _ = otherOrgId;
    }

    [Fact]
    public async Task StaffCaller_SelectingOutOfScopeBranchInOwnOrganization_Returns403()
    {
        if (!_postgresAvailable) return;
        var (organizationId, _, _) = await BootstrapAsync("branch-out-of-scope@example.com", "correct-password");
        var secondBranchId = await AddOutOfScopeBranchAsync(organizationId, "Centro");
        var admin = await SignInAsync("branch-out-of-scope@example.com", "correct-password");

        var response = await admin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches", [(BranchSelectorHeader, secondBranchId.ToString())]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StaffCaller_MalformedBranchHeader_Returns400()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("branch-malformed@example.com", "correct-password");
        var admin = await SignInAsync("branch-malformed@example.com", "correct-password");

        var response = await admin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches", [(BranchSelectorHeader, "not-a-guid")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Sysadmin acting on a selected organization --------------------------

    [Fact]
    public async Task Sysadmin_ActingOnSelectedOrganization_CanSelectAnyBranchOfThatOrganization()
    {
        if (!_postgresAvailable) return;
        var (_, sysadminBranchId, sysadminId) = await BootstrapAsync("sysadmin-branch-any@example.com", "correct-password");
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, targetBranchId, _) = await BootstrapAsync("branch-any-target@example.com", "correct-password");
        var sysadmin = await SignInAsync("sysadmin-branch-any@example.com", "correct-password");

        // targetBranchId is NOT in the sysadmin's own (empty) BranchScope —
        // only reachable via "acting on a selected organization".
        var response = await sysadmin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches",
            [(OrganizationSelectorHeader, targetOrgId.ToString()), (BranchSelectorHeader, targetBranchId.ToString())]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = sysadminBranchId;
    }

    [Fact]
    public async Task Sysadmin_ActingOnSelectedOrganization_SelectingBranchOfADifferentOrganization_Returns403()
    {
        if (!_postgresAvailable) return;
        var (_, _, sysadminId) = await BootstrapAsync("sysadmin-branch-cross@example.com", "correct-password");
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, _, _) = await BootstrapAsync("branch-cross-target-a@example.com", "correct-password");
        var (_, otherOrgBranchId, _) = await BootstrapAsync("branch-cross-target-b@example.com", "correct-password");
        var sysadmin = await SignInAsync("sysadmin-branch-cross@example.com", "correct-password");

        var response = await sysadmin.SendAsync(WithHeaders(HttpMethod.Get, "/account/branches",
            [(OrganizationSelectorHeader, targetOrgId.ToString()), (BranchSelectorHeader, otherOrgBranchId.ToString())]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Device path: header is always ignored --------------------------------

    [Fact]
    public async Task Device_MalformedBranchHeader_IsIgnored_RequestSucceeds()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, userId) = await BootstrapAsync("device-malformed@example.com", "correct-password");

        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(new CloudTenantScope(orgId), Guid.NewGuid(), branchId, userId, CancellationToken.None);

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/sync/inbox");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);
        request.Headers.Add(BranchSelectorHeader, "not-a-guid");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Device_UnknownBranchHeader_IsIgnored_RequestSucceeds()
    {
        if (!_postgresAvailable) return;
        var (orgId, branchId, userId) = await BootstrapAsync("device-unknown-header@example.com", "correct-password");

        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(new CloudTenantScope(orgId), Guid.NewGuid(), branchId, userId, CancellationToken.None);

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/sync/inbox");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);
        request.Headers.Add(BranchSelectorHeader, Guid.NewGuid().ToString());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- GET /account/me: selectableBranches ----------------------------------

    [Fact]
    public async Task Me_ReturnsSelectableBranches_ForOrdinaryBusinessAdmin()
    {
        if (!_postgresAvailable) return;
        var (_, branchId, _) = await BootstrapAsync("me-single-branch@example.com", "correct-password");
        var admin = await SignInAsync("me-single-branch@example.com", "correct-password");

        var response = await admin.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SignedInResponse>();

        var branch = Assert.Single(body!.SelectableBranches);
        Assert.Equal(branchId, branch.Id);
        Assert.Equal("HQ", branch.Name);
    }

    [Fact]
    public async Task Me_ReturnsSelectableBranches_ForMultiBranchUser()
    {
        if (!_postgresAvailable) return;
        var (organizationId, firstBranchId, _) = await BootstrapAsync("me-multi-branch@example.com", "correct-password");
        var secondBranchId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        await orgStore.CreateBranchAsync(new CloudTenantScope(organizationId), new NewBranch(secondBranchId, "Centro"), CancellationToken.None);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var admin = await SignInAsync("me-multi-branch@example.com", "correct-password");
        // Widen the admin's own persisted BranchScope directly (raw SQL —
        // no "add branch to my own scope" endpoint exists yet).
        using var widen = new NpgsqlCommand(
            "UPDATE users SET branch_scope = branch_scope || $1::uuid WHERE organization_id = $2", owner);
        widen.Parameters.AddWithValue(secondBranchId);
        widen.Parameters.AddWithValue(organizationId);
        widen.ExecuteNonQuery();

        var response = await admin.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SignedInResponse>();

        Assert.Equal(2, body!.SelectableBranches.Count);
        Assert.Contains(body.SelectableBranches, b => b.Id == firstBranchId);
        Assert.Contains(body.SelectableBranches, b => b.Id == secondBranchId);
    }

    [Fact]
    public async Task Me_SysadminWithNoSelection_ReturnsEmptySelectableBranches()
    {
        if (!_postgresAvailable) return;
        var (_, _, sysadminId) = await BootstrapAsync("me-sysadmin-noselection@example.com", "correct-password");
        PromoteToSystemAdmin(sysadminId);
        var sysadmin = await SignInAsync("me-sysadmin-noselection@example.com", "correct-password");

        var response = await sysadmin.GetAsync("/account/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SignedInResponse>();

        Assert.Empty(body!.SelectableBranches);
    }

    [Fact]
    public async Task Me_SysadminActingOnSelectedOrganization_ReturnsAllBranchesOfThatOrganization()
    {
        if (!_postgresAvailable) return;
        var (_, _, sysadminId) = await BootstrapAsync("me-sysadmin-selected@example.com", "correct-password");
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, targetBranchId, _) = await BootstrapAsync("me-sysadmin-target@example.com", "correct-password");
        var sysadmin = await SignInAsync("me-sysadmin-selected@example.com", "correct-password");

        var response = await sysadmin.SendAsync(WithHeaders(HttpMethod.Get, "/account/me", [(OrganizationSelectorHeader, targetOrgId.ToString())]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SignedInResponse>();

        var branch = Assert.Single(body!.SelectableBranches);
        Assert.Equal(targetBranchId, branch.Id);
    }

    // --- Shared GUC helper -----------------------------------------------------

    [Fact]
    public async Task TenantScopeSql_SetsBothOrgAndBranchGucs_WhenBranchSelected()
    {
        if (!_postgresAvailable) return;
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(organizationId, BranchId: branchId), CancellationToken.None);

        Assert.Equal(organizationId.ToString(), await ReadSettingAsync(connection, tx, "app.current_org_id"));
        Assert.Equal(branchId.ToString(), await ReadSettingAsync(connection, tx, "app.current_branch_id"));

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task TenantScopeSql_LeavesBranchGucUnset_WhenNoBranchSelected()
    {
        if (!_postgresAvailable) return;
        var organizationId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        await TenantScopeSql.ApplyAsync(connection, tx, new CloudTenantScope(organizationId), CancellationToken.None);

        Assert.Equal(organizationId.ToString(), await ReadSettingAsync(connection, tx, "app.current_org_id"));
        Assert.Equal(string.Empty, await ReadSettingAsync(connection, tx, "app.current_branch_id"));

        await tx.RollbackAsync();
    }

    private static async Task<string> ReadSettingAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string setting)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_setting($1, true)", connection, tx);
        cmd.Parameters.AddWithValue(setting);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? string.Empty : (string)result;
    }
}
