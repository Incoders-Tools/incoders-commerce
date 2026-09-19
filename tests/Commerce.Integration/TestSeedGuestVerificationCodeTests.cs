using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Phase 8 follow-up B (commerce-guest-ordering verify-report.md WARNING 2):
/// the dev-only seam that lets a test read the last guest verification code
/// sent through <see cref="Commerce.Cloud.Api.Email.LogOnlyEmailSender"/>
/// back out over HTTP, mirroring the existing `/internal/test-seed/user`
/// precedent. This covers the SEAM ENDPOINT ITSELF, not the Playwright E2E
/// wiring (that lives in the web branch — see route contract in the
/// endpoint's XML remarks).
/// </summary>
[Collection("Postgres")]
public sealed class TestSeedGuestVerificationCodeTests : IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Guid _organizationId = Guid.NewGuid();

    public TestSeedGuestVerificationCodeTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseSetting("GuestOrdering:OrganizationId", _organizationId.ToString());
            builder.UseSetting("GuestOrdering:BranchId", Guid.NewGuid().ToString());
            // Explicit, not relied on implicitly (WebApplicationFactory's
            // own default is already "Development", but the seam's whole
            // safety property depends on this environment check in
            // Program.cs, so this test asserts it deliberately).
            builder.UseEnvironment("Development");
        });

        if (_postgresAvailable)
        {
            ApplyMigrationsAndReset();
            SeedOrganization();
        }
    }

    private void SeedOrganization()
    {
        using var connection = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Guest Org')", connection);
        cmd.Parameters.AddWithValue(_organizationId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _factory.Dispose();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Commerce.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
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
        Apply("0005_password_recovery.sql");
        Apply("0006_role_taxonomy.sql");
        Apply("0007_platform_administration.sql", "__PLATFORM_READONLY_PASSWORD__", "dev-only-platform-readonly-password");
        Apply("0008_customer_registry.sql");
        Apply("0009_catalog_and_pricing.sql");
        Apply("0010_guest_ordering.sql");

        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE guest_order_verifications, price_list_entries, price_lists, presentations, products, " +
            "customer_ordering_access, customers, password_reset_tokens, user_directory, users, " +
            "device_credentials, branches, organizations RESTART IDENTITY CASCADE",
            owner);
        resetCmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task GuestVerificationCode_AfterVerificationRequested_IsReadableBack()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        const string email = "seam-reader@example.com";

        var requestResponse = await client.PostAsJsonAsync(
            "/public/guest-orders/verification", new GuestVerificationRequest("30111222333", email));
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);

        var codeResponse = await client.GetAsync(
            $"/internal/test-seed/guest-verification-code?contactAddress={Uri.EscapeDataString(email)}");

        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var body = await codeResponse.Content.ReadFromJsonAsync<TestSeedGuestVerificationCodeResponse>();
        Assert.NotNull(body);
        Assert.Matches(@"^\d{6}$", body!.Code);

        // The confirm endpoint accepts this exact code — proves the seam
        // reads back the REAL code, not a decoy.
        var confirmResponse = await client.PostAsJsonAsync(
            "/public/guest-orders/verification/confirm",
            new { verificationId = await ExtractVerificationIdAsync(requestResponse), code = body.Code });
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);
    }

    private static async Task<Guid> ExtractVerificationIdAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<GuestVerificationRequestedResponse>();
        return payload!.VerificationId;
    }

    [Fact]
    public async Task GuestVerificationCode_UnknownAddress_Returns404()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/internal/test-seed/guest-verification-code?contactAddress=never-emailed@example.com");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
