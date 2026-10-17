using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// railway-deploy-pipeline S13: every sign-in (staff `/account/sign-in`, customer `/customer/sign-in`) is limited per
/// real client AND per submitted account, so neither one address nor one targeted account can be brute-forced. Behind
/// Railway's edge the connection address is the proxy for everyone, so the client is the last `X-Forwarded-For` hop.
///
/// Determinism: the windows are minutes long and each test finishes in seconds, so N+1 back-to-back attempts exhaust
/// a window by count, never by timing (<see cref="PublicRateLimitTests"/>'s convention).
/// </summary>
[Collection("Postgres")]
public sealed class SignInRateLimitTests
{
    private const string Password = "correct-horse-battery-staple";

    private static bool _migrated;

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.OwnerConnectionString);

    public SignInRateLimitTests()
    {
        if (!_postgresAvailable)
        {
            return;
        }

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        if (!_migrated)
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
            {
                PostgresTestFixture.ApplyMigration(owner, file);
            }
            _migrated = true;
        }

        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private static WebApplicationFactory<Program> NewFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, string forwardedFor, object body, string path = "/account/sign-in")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string forwardedFor, string email, string password = "wrong-password") =>
        SignInAsync(client, forwardedFor, new { email, password });

    private static async Task SeedStaffUserAsync(string email)
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
        var userStore = new PostgresUserAccountStore(dataSource);
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hash = new PasswordHasher<UserAccount>().HashPassword(new UserAccount(userId, organizationId, [], []), Password);
        var outcome = await new PostgresOrganizationStore(dataSource, userStore).TryCreateBootstrapAsync(
            new CloudTenantScope(organizationId), new NewOrganization(organizationId, "Client"), new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, email, hash, [branchId], [new RoleDto(RoleCatalog.BusinessAdmin, RoleCatalog.BusinessAdminPermissions)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);
    }

    private static void AssertLimited(HttpResponseMessage response, TimeSpan window)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values), "429 without Retry-After.");
        var seconds = int.Parse(Assert.Single(values));
        Assert.InRange(seconds, 1, (int)window.TotalSeconds);
    }

    [Fact]
    public void TheLimits_AreTheDocumentedOnes()
    {
        Assert.Equal(20, SignInRateLimiting.DefaultPerClientPermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), SignInRateLimiting.PerClientWindow);
        Assert.Equal(5, SignInRateLimiting.DefaultPerAccountPermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(15), SignInRateLimiting.PerAccountWindow);
    }

    [Fact]
    public async Task OneClient_BeyondThePerClientLimit_Gets429_WithRetryAfter()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await using var factory = NewFactory();
        var client = NewClient(factory);

        // A different account each time: only the client partition can stop this (password spraying).
        for (var i = 0; i < SignInRateLimiting.DefaultPerClientPermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, "203.0.113.10", $"spray-{i}@example.com")).StatusCode);
        }

        AssertLimited(await SignInAsync(client, "203.0.113.10", "spray-last@example.com"), SignInRateLimiting.PerClientWindow);
    }

    [Fact]
    public async Task OneAccount_FromDifferentClients_IsLimitedByTheAccountPartition()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await SeedStaffUserAsync("victim@example.com");
        await using var factory = NewFactory();
        var client = NewClient(factory);

        for (var i = 0; i < SignInRateLimiting.DefaultPerAccountPermitLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, $"198.51.100.{i + 1}", "victim@example.com")).StatusCode);
        }

        // Exhausted even with the right password and from a fresh address; the case and spacing of the email and the
        // casing of the JSON property do not open a new partition.
        AssertLimited(
            await SignInAsync(client, "198.51.100.200", new { EMAIL = "  Victim@Example.COM ", password = Password }),
            SignInRateLimiting.PerAccountWindow);

        // Another account from the same address is unaffected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, "198.51.100.200", "someone-else@example.com")).StatusCode);
    }

    [Fact]
    public async Task TwoClients_AreLimitedIndependently()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await using var factory = NewFactory(("RateLimits:SignInPerClientPermitLimit", "3"));
        var client = NewClient(factory);

        for (var i = 0; i < 3; i++)
        {
            await SignInAsync(client, "203.0.113.30", $"a-{i}@example.com");
        }
        AssertLimited(await SignInAsync(client, "203.0.113.30", "a-last@example.com"), SignInRateLimiting.PerClientWindow);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, "203.0.113.31", "b@example.com")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheHopAddedByTheEdgeCounts_SoAClientCannotRotateItsPartitionByPrependingAddresses()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await using var factory = NewFactory(("RateLimits:SignInPerClientPermitLimit", "3"));
        var client = NewClient(factory);

        for (var i = 0; i < 3; i++)
        {
            await SignInAsync(client, $"10.0.0.{i + 1}, 203.0.113.40", $"rotate-{i}@example.com");
        }

        AssertLimited(await SignInAsync(client, "10.0.0.99, 203.0.113.40", "rotate-last@example.com"), SignInRateLimiting.PerClientWindow);
    }

    [Fact]
    public async Task ASuccessfulSignIn_UnderTheLimit_StillWorks_AfterAFewMistypes()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await SeedStaffUserAsync("typo@example.com");
        await using var factory = NewFactory();
        var client = NewClient(factory);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, "203.0.113.50", "typo@example.com")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(client, "203.0.113.50", "typo@example.com", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/me")).StatusCode);
    }

    [Fact]
    public async Task CustomerSignIn_IsLimitedPerAccountToo()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await using var factory = NewFactory();
        var client = NewClient(factory);

        for (var i = 0; i < SignInRateLimiting.DefaultPerAccountPermitLimit; i++)
        {
            var attempt = await SignInAsync(client, $"192.0.2.{i + 1}", new { email = "customer@example.com", password = "wrong" }, "/customer/sign-in");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        AssertLimited(
            await SignInAsync(client, "192.0.2.100", new { email = "customer@example.com", password = "wrong" }, "/customer/sign-in"),
            SignInRateLimiting.PerAccountWindow);
    }

    [Fact]
    public async Task OtherAnonymousAccountRoutes_AreNotCountedAsSignIns()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml."); return; }

        await using var factory = NewFactory(("RateLimits:SignInPerClientPermitLimit", "1"));
        var client = NewClient(factory);

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/account/reset-password/confirm")
            {
                Content = JsonContent.Create(new { token = "x", newPassword = "y" }),
            };
            request.Headers.Add("X-Forwarded-For", "203.0.113.60");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(client, "203.0.113.60", "first@example.com")).StatusCode);
    }

    [Fact]
    public async Task TheExistingPerIpLimiters_NowPartitionByTheRealClient()
    {
        // `/device/pair` uses the same IpPartitionKey as the `/public` policies.
        await using var factory = NewFactory(("RateLimits:DevicePairPermitLimit", "2"));
        var client = NewClient(factory);

        async Task<HttpStatusCode> PairAsync(string forwardedFor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/device/pair")
            {
                Content = JsonContent.Create(new DevicePairRequest("", "", Guid.NewGuid(), null)),
            };
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return (await client.SendAsync(request)).StatusCode;
        }

        await PairAsync("203.0.113.70");
        await PairAsync("203.0.113.70");
        Assert.Equal(HttpStatusCode.TooManyRequests, await PairAsync("203.0.113.70"));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PairAsync("203.0.113.71"));
    }
}
