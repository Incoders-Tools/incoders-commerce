using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Sync;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// odd/tasks/organization-account-standing.md T4: while an organization is Suspended its web users get
/// 403 <c>organization-suspended</c> on every business endpoint, except sign-in, sign-out and <c>/account/me</c>; the
/// system administrator is never blocked; the POS (device bearer) is never touched. <c>/account/me</c> carries the
/// standing, with the dates only for administrators (<see cref="Permission.ManageUsers"/>) and the system administrator.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationSuspensionEnforcementTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private const string BusinessEndpoint = "/account/branches";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public OrganizationSuspensionEnforcementTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString).UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrations();
    }

    public void Dispose() => _factory.Dispose();

    private DateOnly Today => _factory.Services.GetRequiredService<IBusinessClock>().Today;

    private static void ApplyMigrations()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        foreach (var file in Directory.GetFiles(Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations"), "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            PostgresTestFixture.ApplyMigration(owner, file);
    }

    private static void Owner(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private sealed record Tenant(Guid OrganizationId, Guid BranchId, string AdminEmail, string CashierEmail);

    /// <summary>An organization with its business admin (bootstrap) and a cashier who only operates the POS.</summary>
    private async Task<Tenant> NewTenantAsync()
    {
        var organizationId = Guid.NewGuid();
        var adminEmail = $"suspension-admin-{organizationId:N}@example.com";
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", adminEmail, Password));
        response.EnsureSuccessStatusCode();
        var bootstrap = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;

        var cashierId = Guid.NewGuid();
        var cashierEmail = $"suspension-cashier-{organizationId:N}@example.com";
        var hash = _factory.Services.GetRequiredService<PasswordHasher<UserAccount>>().HashPassword(new UserAccount(cashierId, organizationId, [], []), Password);
        var roles = JsonSerializer.Serialize(new[] { new RoleDto("cashier", Permission.OperatePos) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Owner("INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1,$2,$3,$4,$5,$6::jsonb)",
            cashierId, organizationId, cashierEmail, hash, new[] { bootstrap.BranchId }, roles);
        Owner("INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1,$2,$3)", cashierEmail, organizationId, cashierId);

        return new Tenant(organizationId, bootstrap.BranchId, adminEmail, cashierEmail);
    }

    /// <summary>A system administrator, from an organization of its own.</summary>
    private async Task<HttpClient> SysadminAsync() => (await SysadminWithOrganizationAsync()).Client;

    private async Task<(HttpClient Client, Guid OrganizationId)> SysadminWithOrganizationAsync()
    {
        var tenant = await NewTenantAsync();
        Owner("UPDATE users SET is_system_admin = true WHERE email = $1", tenant.AdminEmail);
        return (await SignInAsync(tenant.AdminEmail), tenant.OrganizationId);
    }

    private HttpClient NewBrowser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = NewBrowser();
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    private static void SetStanding(Guid organizationId, DateOnly? dueOn, bool suspended) =>
        Owner("UPDATE organizations SET billing_due_on = $2, billing_grace_days = 30, suspended_at = CASE WHEN $3 THEN now() ELSE NULL END WHERE id = $1",
            organizationId, dueOn is { } due ? due : DBNull.Value, suspended);

    private static async Task AssertSuspendedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("organization-suspended", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SuspendedOrganization_ItsAdmin_IsBlockedOnBusinessEndpoints_ButSignsIn_ReadsMe_AndSignsOut()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        SetStanding(tenant.OrganizationId, null, suspended: true);

        var admin = await SignInAsync(tenant.AdminEmail);

        await AssertSuspendedAsync(await admin.GetAsync(BusinessEndpoint));
        var me = await admin.GetFromJsonAsync<SignedInResponse>("/account/me");
        Assert.Equal("Suspended", me!.AccountStanding!.Status);
        Assert.True((await admin.PostAsync("/account/sign-out", null)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task SuspendedOrganization_ItsCashier_IsBlockedToo_AndSeesTheStatusWithoutDates()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        SetStanding(tenant.OrganizationId, Today.AddDays(-40), suspended: false);

        var cashier = await SignInAsync(tenant.CashierEmail);

        await AssertSuspendedAsync(await cashier.GetAsync(BusinessEndpoint));
        var me = await cashier.GetFromJsonAsync<SignedInResponse>("/account/me");
        Assert.Equal("Suspended", me!.AccountStanding!.Status);
        Assert.Null(me.AccountStanding.SuspendsOn);
        Assert.Null(me.AccountStanding.DaysLeft);
    }

    [Fact]
    public async Task OverdueOrganization_IsNotBlocked_AndOnlyTheAdminSeesTheCountdown()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        SetStanding(tenant.OrganizationId, Today.AddDays(-1), suspended: false);

        var admin = await SignInAsync(tenant.AdminEmail);
        var cashier = await SignInAsync(tenant.CashierEmail);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(BusinessEndpoint)).StatusCode);
        var adminMe = await admin.GetFromJsonAsync<SignedInResponse>("/account/me");
        Assert.Equal("Overdue", adminMe!.AccountStanding!.Status);
        Assert.Equal(30, adminMe.AccountStanding.DaysLeft);
        Assert.Equal(Today.AddDays(30), adminMe.AccountStanding.SuspendsOn);

        var cashierMe = await cashier.GetFromJsonAsync<SignedInResponse>("/account/me");
        Assert.Equal("Overdue", cashierMe!.AccountStanding!.Status);
        Assert.Null(cashierMe.AccountStanding.DaysLeft);
        Assert.Null(cashierMe.AccountStanding.SuspendsOn);
    }

    [Fact]
    public async Task SignIn_CarriesTheSameStandingAsMe_BecauseTheSpaKeepsTheSignInResponseAsItsSession()
    {
        if (!_postgresAvailable) return;
        var overdue = await NewTenantAsync();
        SetStanding(overdue.OrganizationId, Today.AddDays(-1), suspended: false);
        var suspended = await NewTenantAsync();
        SetStanding(suspended.OrganizationId, null, suspended: true);

        var adminSignIn = await (await NewBrowser().PostAsJsonAsync("/account/sign-in", new SignInRequest(overdue.AdminEmail, Password)))
            .Content.ReadFromJsonAsync<SignedInResponse>();
        var cashierSignIn = await (await NewBrowser().PostAsJsonAsync("/account/sign-in", new SignInRequest(overdue.CashierEmail, Password)))
            .Content.ReadFromJsonAsync<SignedInResponse>();
        var suspendedSignIn = await NewBrowser().PostAsJsonAsync("/account/sign-in", new SignInRequest(suspended.AdminEmail, Password));

        Assert.Equal("Overdue", adminSignIn!.AccountStanding!.Status);
        Assert.Equal(30, adminSignIn.AccountStanding.DaysLeft);
        Assert.Equal("Overdue", cashierSignIn!.AccountStanding!.Status);
        Assert.Null(cashierSignIn.AccountStanding.DaysLeft);
        Assert.Equal(HttpStatusCode.OK, suspendedSignIn.StatusCode);
        Assert.Equal("Suspended", (await suspendedSignIn.Content.ReadFromJsonAsync<SignedInResponse>())!.AccountStanding!.Status);
    }

    [Fact]
    public async Task ActiveOrganization_MeReportsActive()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();

        var admin = await SignInAsync(tenant.AdminEmail);

        Assert.Equal("Active", (await admin.GetFromJsonAsync<SignedInResponse>("/account/me"))!.AccountStanding!.Status);
    }

    [Fact]
    public async Task Sysadmin_ActingOnASuspendedOrganization_IsNotBlocked_EvenWhenItsOwnOrganizationIsSuspended()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        SetStanding(tenant.OrganizationId, null, suspended: true);
        var (sysadmin, sysadminOrganizationId) = await SysadminWithOrganizationAsync();
        // The block reads the organization of the caller's cookie, so suspend the sysadmin's own one too: only the
        // system-administrator exemption can let this request through.
        SetStanding(sysadminOrganizationId, null, suspended: true);

        var request = new HttpRequestMessage(HttpMethod.Get, BusinessEndpoint);
        request.Headers.Add(TenantScopeEndpointFilter.OrganizationSelectorHeader, tenant.OrganizationId.ToString());

        Assert.Equal(HttpStatusCode.OK, (await sysadmin.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task SuspendingAndReactivatingThroughTheApi_TakesEffectOnTheVeryNextRequest()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        var admin = await SignInAsync(tenant.AdminEmail);
        var sysadmin = await SysadminAsync();
        // Warm the standing cache with Active before the change.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(BusinessEndpoint)).StatusCode);

        await sysadmin.PostAsync($"/account/organizations/{tenant.OrganizationId}/standing/suspend", null);
        await AssertSuspendedAsync(await admin.GetAsync(BusinessEndpoint));

        await sysadmin.PostAsJsonAsync($"/account/organizations/{tenant.OrganizationId}/standing/reactivate", new ReactivateOrganizationRequest(Today.AddDays(30), null));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(BusinessEndpoint)).StatusCode);
    }

    [Fact]
    public async Task SuspendedOrganization_ItsPos_KeepsSyncingAndReadingSettings()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        SetStanding(tenant.OrganizationId, null, suspended: true);
        using var scope = _factory.Services.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(tenant.OrganizationId), Guid.NewGuid(), tenant.BranchId, Guid.NewGuid(), CancellationToken.None);
        var pos = _factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.PlaintextToken);

        Assert.Equal(HttpStatusCode.OK, (await pos.GetAsync("/device/organization/settings")).StatusCode);
        var push = await pos.PostAsJsonAsync("/sync/inbox", new SyncEnvelope(
            OperationId: Guid.NewGuid(), ContractVersion: 1, OrganizationId: tenant.OrganizationId, BranchId: tenant.BranchId,
            AggregateId: Guid.NewGuid(), AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
            OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "suspension-probe", Payload: "{\"probe\":true}"));
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
    }

    [Fact]
    public async Task APermissionDenial_IsStillAPlain403_WithoutTheSuspensionCode()
    {
        if (!_postgresAvailable) return;
        var tenant = await NewTenantAsync();
        var cashier = await SignInAsync(tenant.CashierEmail);

        var response = await cashier.GetAsync("/account/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("organization-suspended", await response.Content.ReadAsStringAsync());
    }
}
