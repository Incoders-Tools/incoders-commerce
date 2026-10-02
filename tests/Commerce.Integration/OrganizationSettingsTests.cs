using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// `0035_organization_settings.sql` and `/account/organization/settings`: the organization-level number format
/// (`quantityDecimalSeparator`: Comma | Dot), the first field of the organization settings.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationSettingsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public OrganizationSettingsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString).UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrations();
    }

    public void Dispose() => _factory.Dispose();

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

    private async Task<(Guid OrganizationId, Guid UserId)> BootstrapAsync(string email)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        return (organizationId, (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!.UserId);
    }

    private void SeedStaff(Guid organizationId, string email, Permission permissions)
    {
        var userId = Guid.NewGuid();
        var hash = _factory.Services.GetRequiredService<PasswordHasher<UserAccount>>().HashPassword(new UserAccount(userId, organizationId, [], []), Password);
        var roles = JsonSerializer.Serialize(new[] { new RoleDto("staff", permissions) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using (var user = new NpgsqlCommand("INSERT INTO users (id, organization_id, email, password_hash, branch_scope, roles) VALUES ($1,$2,$3,$4,$5,$6::jsonb)", owner))
        {
            user.Parameters.AddWithValue(userId); user.Parameters.AddWithValue(organizationId); user.Parameters.AddWithValue(email);
            user.Parameters.AddWithValue(hash); user.Parameters.AddWithValue(Array.Empty<Guid>()); user.Parameters.AddWithValue(roles); user.ExecuteNonQuery();
        }
        using var directory = new NpgsqlCommand("INSERT INTO user_directory (email_normalized, organization_id, user_id) VALUES ($1,$2,$3)", owner);
        directory.Parameters.AddWithValue(email); directory.Parameters.AddWithValue(organizationId); directory.Parameters.AddWithValue(userId); directory.ExecuteNonQuery();
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public void Migration_AddsTheSeparatorColumn_DefaultComma_RejectsOtherValues_AndRerunsSafely()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        PostgresTestFixture.ApplyMigration(owner, "0035_organization_settings.sql");
        PostgresTestFixture.ApplyMigration(owner, "0035_organization_settings.sql");

        var id = Guid.NewGuid();
        using (var insert = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Settings default')", owner)) { insert.Parameters.AddWithValue(id); insert.ExecuteNonQuery(); }
        Assert.Equal("Comma", OwnerScalar("SELECT quantity_decimal_separator FROM organizations WHERE id = $1", id));
        Assert.Throws<PostgresException>(() =>
        {
            using var bad = new NpgsqlCommand("UPDATE organizations SET quantity_decimal_separator = 'Semicolon' WHERE id = $1", owner);
            bad.Parameters.AddWithValue(id); bad.ExecuteNonQuery();
        });
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheOrganizationSettingsMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0035_organization_settings.sql"))), init);
    }

    [Fact]
    public async Task Settings_DefaultToComma_BusinessAdminSetsDot_AndTheChangeIsAudited()
    {
        if (!_postgresAvailable) return;
        var (organizationId, userId) = await BootstrapAsync("settings-admin@example.com");
        var admin = await SignInAsync("settings-admin@example.com");

        var initial = await admin.GetFromJsonAsync<OrganizationSettingsResponse>("/account/organization/settings");
        Assert.Equal("Comma", initial!.QuantityDecimalSeparator);

        var put = await admin.PutAsJsonAsync("/account/organization/settings", new UpdateOrganizationSettingsRequest("Dot"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("Dot", (await admin.GetFromJsonAsync<OrganizationSettingsResponse>("/account/organization/settings"))!.QuantityDecimalSeparator);
        Assert.Equal(1L, OwnerScalar("SELECT count(*) FROM audit_log WHERE actor_id = $1 AND organization_id = $2 AND action = 'organization.settings_updated'", userId, organizationId));
    }

    [Fact]
    public async Task Settings_RejectUnknownValues_AndCallersWithoutManageBranchSettings_ButAnyStaffMayRead()
    {
        if (!_postgresAvailable) return;
        var (organizationId, _) = await BootstrapAsync("settings-gate-admin@example.com");
        SeedStaff(organizationId, "settings-gate-staff@example.com", Permission.ViewSales);
        var admin = await SignInAsync("settings-gate-admin@example.com");
        var staff = await SignInAsync("settings-gate-staff@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/account/organization/settings", new UpdateOrganizationSettingsRequest("Semicolon"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/account/organization/settings", new UpdateOrganizationSettingsRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/account/organization/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync("/account/organization/settings", new UpdateOrganizationSettingsRequest("Dot"))).StatusCode);
        Assert.Equal("Comma", (await admin.GetFromJsonAsync<OrganizationSettingsResponse>("/account/organization/settings"))!.QuantityDecimalSeparator);
    }

    [Fact]
    public async Task Settings_OfOneOrganization_NeverLeakIntoAnother()
    {
        if (!_postgresAvailable) return;
        await BootstrapAsync("settings-iso-a@example.com");
        await BootstrapAsync("settings-iso-b@example.com");
        var a = await SignInAsync("settings-iso-a@example.com");
        var b = await SignInAsync("settings-iso-b@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await a.PutAsJsonAsync("/account/organization/settings", new UpdateOrganizationSettingsRequest("Dot"))).StatusCode);
        Assert.Equal("Comma", (await b.GetFromJsonAsync<OrganizationSettingsResponse>("/account/organization/settings"))!.QuantityDecimalSeparator);
    }
}
