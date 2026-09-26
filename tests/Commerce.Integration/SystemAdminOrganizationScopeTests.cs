using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// platform-administration spec, "Sysadmin Acts On A Selected Organization":
/// a caller holding cross-org sysadmin capability selects a target
/// organization via the <c>X-Organization-Id</c> header and, for that
/// request only, exercises tenant modules (starting with branches) on the
/// target organization with the full staff permission set. Reuses
/// <see cref="AdminConsoleTests"/>'s exact fixture shape (bootstrap +
/// raw-SQL sysadmin promotion) rather than duplicating a new one.
/// </summary>
[Collection("Postgres")]
public sealed class SystemAdminOrganizationScopeTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string OrganizationSelectorHeader = "X-Organization-Id";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public SystemAdminOrganizationScopeTests(WebApplicationFactory<Program> factory)
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

    private async Task<(Guid OrganizationId, Guid UserId)> BootstrapAsync(string email, string password)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, password));
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static void PromoteToSystemAdmin(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb WHERE id = $1", owner);
        flag.Parameters.AddWithValue(userId);
        flag.ExecuteNonQuery();
    }

    private static HttpRequestMessage WithOrganizationSelector(HttpMethod method, string url, Guid organizationId, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(OrganizationSelectorHeader, organizationId.ToString());
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Sysadmin_WithSelector_CreatesAndListsBranchesInTargetOrganization()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (sysadminOrgId, sysadminId) = await BootstrapAsync("sysadmin-branch@example.com", password);
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, _) = await BootstrapAsync("target-org-admin@example.com", password);

        var sysadmin = await SignInAsync("sysadmin-branch@example.com", password);

        var create = await sysadmin.SendAsync(WithOrganizationSelector(
            HttpMethod.Post, "/account/branches", targetOrgId, new CreateBranchRequest("Sysadmin-Created Branch")));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await sysadmin.SendAsync(WithOrganizationSelector(HttpMethod.Get, "/account/branches", targetOrgId));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var branches = await list.Content.ReadFromJsonAsync<List<BranchSummaryDto>>();
        Assert.Contains(branches!, b => b.BranchName == "Sysadmin-Created Branch");

        _ = sysadminOrgId; // the sysadmin's own (hidden) org — asserted empty below.
    }

    [Fact]
    public async Task Sysadmin_WithoutSelector_SeesNoTenantDataForOwnOrganization()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (_, sysadminId) = await BootstrapAsync("sysadmin-noselector@example.com", password);
        PromoteToSystemAdmin(sysadminId);

        var sysadmin = await SignInAsync("sysadmin-noselector@example.com", password);

        // Unchanged pre-existing behavior: a sysadmin's own (hidden) org row
        // holds zero roles, so ManageBranchSettings is denied like any other
        // zero-permission caller — rejected outright, not silently narrowed
        // to an empty list (same fail-closed philosophy as the
        // Organizations endpoint elsewhere in this spec).
        var list = await sysadmin.GetAsync("/account/branches");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
    }

    [Fact]
    public async Task Sysadmin_SelectingUnknownOrganization_IsRejected()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (_, sysadminId) = await BootstrapAsync("sysadmin-unknown-org@example.com", password);
        PromoteToSystemAdmin(sysadminId);
        var sysadmin = await SignInAsync("sysadmin-unknown-org@example.com", password);

        var response = await sysadmin.SendAsync(WithOrganizationSelector(
            HttpMethod.Post, "/account/branches", Guid.NewGuid(), new CreateBranchRequest("Should Not Be Created")));

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"expected 404 or 400 for an unknown selected organization, got {response.StatusCode}");
    }

    [Fact]
    public async Task NonSysadminSelector_IsIgnored_RequestStaysScopedToCallersOwnOrganization()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (organizationBId, _) = await BootstrapAsync("business-admin-b@example.com", password);
        var (organizationAId, _) = await BootstrapAsync("target-org-a-admin@example.com", password);

        var adminB = await SignInAsync("business-admin-b@example.com", password);

        var create = await adminB.SendAsync(WithOrganizationSelector(
            HttpMethod.Post, "/account/branches", organizationAId, new CreateBranchRequest("Leaked Into A?")));
        // Ignored selector: business-admin B still acts on their OWN org (B),
        // so the write succeeds — just never against A.
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var listA = await adminB.SendAsync(WithOrganizationSelector(HttpMethod.Get, "/account/branches", organizationAId));
        var branchesSeenByB = await listA.Content.ReadFromJsonAsync<List<BranchSummaryDto>>();
        Assert.Contains(branchesSeenByB!, b => b.BranchName == "Leaked Into A?");

        // Prove isolation from the OTHER side: A's own admin never sees B's write.
        var adminA = await SignInAsync("target-org-a-admin@example.com", password);
        var listFromA = await adminA.GetAsync("/account/branches");
        var branchesOfA = await listFromA.Content.ReadFromJsonAsync<List<BranchSummaryDto>>();
        Assert.DoesNotContain(branchesOfA!, b => b.BranchName == "Leaked Into A?");
        _ = organizationBId;
    }

    [Fact]
    public async Task Sysadmin_ActingOnSelectedOrganization_WriteIsAuditedWithSysadminAsActor()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (_, sysadminId) = await BootstrapAsync("sysadmin-audit@example.com", password);
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, _) = await BootstrapAsync("audited-target-admin@example.com", password);
        var sysadmin = await SignInAsync("sysadmin-audit@example.com", password);

        var create = await sysadmin.SendAsync(WithOrganizationSelector(
            HttpMethod.Post, "/account/branches", targetOrgId, new CreateBranchRequest("Audited Branch")));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE actor_id = $1 AND organization_id = $2 AND action = 'branch.created'", owner);
        cmd.Parameters.AddWithValue(sysadminId);
        cmd.Parameters.AddWithValue(targetOrgId);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Sysadmin_ActingOnSelectedOrganization_CanManageUsersOfThatOrganization()
    {
        if (!_postgresAvailable) return;
        const string password = "correct-password";
        var (_, sysadminId) = await BootstrapAsync("sysadmin-users@example.com", password);
        PromoteToSystemAdmin(sysadminId);
        var (targetOrgId, targetAdminId) = await BootstrapAsync("target-users-admin@example.com", password);
        var sysadmin = await SignInAsync("sysadmin-users@example.com", password);

        var list = await sysadmin.SendAsync(WithOrganizationSelector(HttpMethod.Get, "/account/users", targetOrgId));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var users = await list.Content.ReadFromJsonAsync<List<UserSummaryDto>>();
        Assert.Contains(users!, u => u.UserId == targetAdminId);
    }
}
