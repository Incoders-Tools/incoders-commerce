using System.Net;
using System.Net.Http.Json;
using Commerce.Application.Time;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// odd/tasks/organization-account-standing.md T3: the system administrator sets an organization's billing due date and
/// grace days, suspends it by hand and reactivates it, and reads the standing derived from those inputs on the business
/// day. Every write is audited; nobody else reaches these routes.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationAccountStandingEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public OrganizationAccountStandingEndpointTests(WebApplicationFactory<Program> factory)
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

    private static object? OwnerScalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd.ExecuteScalar();
    }

    private async Task<(Guid OrganizationId, Guid UserId, string Email)> BootstrapAsync(string prefix)
    {
        var organizationId = Guid.NewGuid();
        var email = $"{prefix}-{organizationId:N}@example.com";
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        return (organizationId, (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!.UserId, email);
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A system administrator (signed in) and a separate target organization whose admin is ordinary.</summary>
    private async Task<(HttpClient Sysadmin, Guid SysadminId, Guid TargetId, string TargetAdminEmail)> ArrangeAsync()
    {
        var (_, sysadminId, sysadminEmail) = await BootstrapAsync("standing-sysadmin");
        OwnerScalar("UPDATE users SET is_system_admin = true WHERE id = $1", sysadminId);
        var (targetId, _, targetAdminEmail) = await BootstrapAsync("standing-target");
        return (await SignInAsync(sysadminEmail), sysadminId, targetId, targetAdminEmail);
    }

    private static long AuditCount(Guid actorId, Guid organizationId, string action) =>
        (long)OwnerScalar("SELECT count(*) FROM audit_log WHERE actor_id = $1 AND organization_id = $2 AND action = $3", actorId, organizationId, action)!;

    [Fact]
    public async Task NewOrganization_IsActive_WithThirtyGraceDays_AndNoDueDate()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();

        var standing = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");

        Assert.Equal("Active", standing!.Status);
        Assert.Null(standing.DueOn);
        Assert.Equal(30, standing.GraceDays);
        Assert.Null(standing.SuspendedAt);
        Assert.Null(standing.SuspendsOn);
        Assert.Null(standing.DaysLeft);
    }

    [Fact]
    public async Task Sysadmin_SetsADueDateOfYesterday_AndTheOrganizationIsOverdueWithThirtyDaysLeft_Audited()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, sysadminId, targetId, _) = await ArrangeAsync();

        var put = await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing",
            new UpdateOrganizationAccountStandingRequest(Today.AddDays(-1), 30));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var standing = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal("Overdue", standing!.Status);
        Assert.Equal(Today.AddDays(-1), standing.DueOn);
        Assert.Equal(Today.AddDays(30), standing.SuspendsOn);
        Assert.Equal(30, standing.DaysLeft);
        Assert.Equal(1L, AuditCount(sysadminId, targetId, "organization.standing_updated"));
    }

    [Fact]
    public async Task Sysadmin_ClearsTheDueDate_AndTheOrganizationIsActiveAgain()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(Today.AddDays(-40), 30));
        // Precondition: 40 days past due with 30 of grace is already suspended, so "Active" below is the clearing's doing.
        Assert.Equal("Suspended", (await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing"))!.Status);

        await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(null, 30));

        var standing = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal("Active", standing!.Status);
        Assert.Null(standing.DueOn);
    }

    [Fact]
    public async Task Sysadmin_SuspendsNow_ThenReactivatesWithANewDueDate_BothAudited()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, sysadminId, targetId, _) = await ArrangeAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await sysadmin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null)).StatusCode);
        var suspended = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal("Suspended", suspended!.Status);
        Assert.NotNull(suspended.SuspendedAt);
        Assert.Equal(1L, AuditCount(sysadminId, targetId, "organization.suspended"));

        var reactivate = await sysadmin.PostAsJsonAsync($"/account/organizations/{targetId}/standing/reactivate",
            new ReactivateOrganizationRequest(Today.AddDays(30), null));
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);

        var active = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal("Active", active!.Status);
        Assert.Null(active.SuspendedAt);
        Assert.Equal(Today.AddDays(30), active.DueOn);
        Assert.Equal(30, active.GraceDays);
        Assert.Equal(1L, AuditCount(sysadminId, targetId, "organization.reactivated"));
    }

    [Fact]
    public async Task SuspendingAnAlreadySuspendedOrganization_KeepsTheOriginalSuspensionTime()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        // A suspension well in the past, so a second call that overwrote it with now() could not compare equal.
        var original = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        OwnerScalar("UPDATE organizations SET suspended_at = $2 WHERE id = $1", targetId, original);

        Assert.Equal(HttpStatusCode.NoContent, (await sysadmin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null)).StatusCode);

        Assert.Equal(original, (await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing"))!.SuspendedAt);
    }

    [Fact]
    public async Task SettingTheStanding_WithoutGraceDays_IsRejected_AndLeavesTheGraceDaysAlone()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(Today.AddDays(-1), 45));

        // A body without graceDays must not be read as 0, which would suspend an overdue organization on the spot.
        var put = await sysadmin.PutAsync($"/account/organizations/{targetId}/standing",
            JsonContent.Create(new { dueOn = Today.AddDays(-1) }));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var standing = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal(45, standing!.GraceDays);
        Assert.Equal("Overdue", standing.Status);
    }

    [Fact]
    public async Task Reactivating_WithoutGraceDays_KeepsTheOrganizationsCurrentGraceDays()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        // 45, not the default 30, so keeping the current value is distinguishable from falling back to the default.
        await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(null, 45));
        await sysadmin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null);

        await sysadmin.PostAsJsonAsync($"/account/organizations/{targetId}/standing/reactivate", new ReactivateOrganizationRequest(Today.AddDays(30), null));

        var standing = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Equal("Active", standing!.Status);
        Assert.Equal(45, standing.GraceDays);
        // The audit row records the grace days actually stored, not the omitted value.
        Assert.Equal(45, (int)OwnerScalar(
            "SELECT (new_value->>'graceDays')::int FROM audit_log WHERE organization_id = $1 AND action = 'organization.reactivated'", targetId)!);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task GraceDaysOutsideZeroToNinety_AreRejected(int graceDays)
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();

        var put = await sysadmin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(Today, graceDays));
        var reactivate = await sysadmin.PostAsJsonAsync($"/account/organizations/{targetId}/standing/reactivate", new ReactivateOrganizationRequest(Today, graceDays));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reactivate.StatusCode);
        var unchanged = await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing");
        Assert.Null(unchanged!.DueOn);
        Assert.Equal(30, unchanged.GraceDays);
    }

    [Fact]
    public async Task Reactivating_WithADueDateInThePast_IsRejected_AndTheOrganizationStaysSuspended()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        await sysadmin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null);

        var reactivate = await sysadmin.PostAsJsonAsync($"/account/organizations/{targetId}/standing/reactivate", new ReactivateOrganizationRequest(Today.AddDays(-1), null));

        Assert.Equal(HttpStatusCode.BadRequest, reactivate.StatusCode);
        Assert.Equal("Suspended", (await sysadmin.GetFromJsonAsync<OrganizationAccountStandingResponse>($"/account/organizations/{targetId}/standing"))!.Status);
    }

    [Fact]
    public async Task UnknownOrganization_Is404_OnEveryRoute()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, _, _) = await ArrangeAsync();
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync($"/account/organizations/{unknown}/standing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.PutAsJsonAsync($"/account/organizations/{unknown}/standing", new UpdateOrganizationAccountStandingRequest(Today, 30))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.PostAsync($"/account/organizations/{unknown}/standing/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.PostAsJsonAsync($"/account/organizations/{unknown}/standing/reactivate", new ReactivateOrganizationRequest(Today, null))).StatusCode);
    }

    [Fact]
    public async Task TheOrganizationsOwnAdmin_CannotReadOrChangeItsStanding()
    {
        if (!_postgresAvailable) return;
        var (_, _, targetId, targetAdminEmail) = await ArrangeAsync();
        var admin = await SignInAsync(targetAdminEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"/account/organizations/{targetId}/standing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/account/organizations/{targetId}/standing", new UpdateOrganizationAccountStandingRequest(null, 90))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/account/organizations/{targetId}/standing/reactivate", new ReactivateOrganizationRequest(Today, null))).StatusCode);
    }

    [Fact]
    public async Task OrganizationsList_CarriesEachOrganizationsStanding()
    {
        if (!_postgresAvailable) return;
        var (sysadmin, _, targetId, _) = await ArrangeAsync();
        await sysadmin.PostAsync($"/account/organizations/{targetId}/standing/suspend", null);

        var organizations = await sysadmin.GetFromJsonAsync<List<OrganizationSummary>>("/account/organizations");

        var target = Assert.Single(organizations!, organization => organization.Id == targetId);
        Assert.Equal("Suspended", target.Standing!.Status);
    }
}
