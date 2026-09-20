using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Payments;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// Covers Unit 4 tasks 4.10-4.14 (commerce-payments design.md "Endpoint
/// shape and authorization", Threat Matrix Routing row): auth/tenancy on
/// `/payments`, fail-closed 503, happy-path record + reversal (never
/// DELETE), and reporting-only settlement reads.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public PaymentEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            // Fail-closed default asserted by ...GatewayUnavailable_Returns503;
            // the ...HappyPath test overrides this per-factory instance below.
            builder.UseSetting("Payments:ManuallyRecordedApprovalEnabled", "false");
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
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var migrationsDir = Path.Combine(RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in new[]
        {
            "0001_init_rls.sql", "0002_users.sql", "0003_organizations_branches.sql",
            "0004_device_credentials.sql", "0005_password_recovery.sql", "0006_role_taxonomy.sql",
            "0007_platform_administration.sql", "0008_customer_registry.sql", "0009_catalog_and_pricing.sql",
            "0010_guest_ordering.sql", "0011_payments.sql"
        })
        {
            var sql = File.ReadAllText(Path.Combine(migrationsDir, file));
            if (file == "0001_init_rls.sql")
            {
                sql = sql.Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
            }
            using var cmd = new NpgsqlCommand(sql, owner);
            cmd.ExecuteNonQuery();
        }

        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE payment_entries, user_directory, users, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task SeedOrganizationAsync(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        await owner.OpenAsync();
        using var cmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Test Org')", owner);
        cmd.Parameters.AddWithValue(organizationId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Mirrors CatalogEndpointTests' real sign-in helper.</summary>
    private async Task<(HttpClient client, Guid organizationId)> SignedInClientAsync(
        WebApplicationFactory<Program> factory, Permission permissions = Permission.None)
    {
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedOrganizationAsync(organizationId);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var newUser = new NewUserAccount(
                userId, $"{userId}@example.com", "unused-hash", new[] { Guid.NewGuid() },
                new[] { new RoleDto("test-role", permissions) });
            var tenantScope = new CloudTenantScope(organizationId);
            var created = await store.TryCreateAsync(tenantScope, newUser, CancellationToken.None);
            Assert.True(created);
        }

        var client = await SignInViaTestEndpointAsync(factory, organizationId, userId);
        return (client, organizationId);
    }

    private async Task<HttpClient> SignInViaTestEndpointAsync(WebApplicationFactory<Program> factory, Guid organizationId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<Commerce.Domain.Identity.UserAccount>>();

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var hash = hasher.HashPassword(new Commerce.Domain.Identity.UserAccount(userId, organizationId, [], []), "test-password");
        using var cmd = new NpgsqlCommand("UPDATE users SET password_hash = $1 WHERE id = $2", owner);
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(userId);
        cmd.ExecuteNonQuery();

        using var emailCmd = new NpgsqlCommand("SELECT email FROM users WHERE id = $1", owner);
        emailCmd.Parameters.AddWithValue(userId);
        var email = (string)(await emailCmd.ExecuteScalarAsync())!;

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });
        var signInResponse = await client.PostAsJsonAsync("/account/sign-in", new { email, password = "test-password" });
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
        return client;
    }

    [Fact]
    public async Task PostPayments_Unauthenticated_Returns401()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/payments/", new
        {
            entryId = Guid.NewGuid(),
            subjectKind = "Order",
            subjectId = Guid.NewGuid(),
            method = "Cash",
            amount = 10m,
            actorId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The request DTO has no organization-naming member for the caller to
    /// populate — organization always resolved via TenantScopeEndpointFilter.
    /// </summary>
    [Fact]
    public void RecordPaymentRequest_HasNoOrganizationNamingMember()
    {
        var memberNames = typeof(RecordPaymentRequest).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(memberNames, name => name.Contains("Organization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostPayments_GatewayUnavailable_Returns503_NoRowInserted()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        var (client, organizationId) = await SignedInClientAsync(_factory);

        var response = await client.PostAsJsonAsync("/payments/", new
        {
            entryId = Guid.NewGuid(),
            subjectKind = "Order",
            subjectId = Guid.NewGuid(),
            method = "Cash",
            amount = 10m,
            actorId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var countCmd = new NpgsqlCommand(
            "SELECT count(*) FROM payment_entries WHERE organization_id = $1", owner);
        countCmd.Parameters.AddWithValue(organizationId);
        var count = (long)countCmd.ExecuteScalar()!;
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Also covers task 4.13: the signed-in actor holds NO special
    /// permission (<see cref="Permission.None"/> — the same authorization
    /// level `OrderingEndpoints`' <c>.RequireAuthorization()</c> requires for
    /// order management) and can both record AND reverse without a second
    /// approver (answered product question (c)). The unauthenticated half of
    /// 4.13 is <see cref="PostPayments_Unauthenticated_Returns401"/>.
    /// </summary>
    [Fact]
    public async Task PostPayments_HappyPath_InsertsEntry_AndReversal_NeverDeletes()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var approvedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseSetting("Payments:ManuallyRecordedApprovalEnabled", "true");
        });

        var (client, _) = await SignedInClientAsync(approvedFactory);
        var entryId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();

        var recordResponse = await client.PostAsJsonAsync("/payments/", new
        {
            entryId,
            subjectKind = "Order",
            subjectId,
            method = "Cash",
            amount = 25m,
            actorId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.OK, recordResponse.StatusCode);
        var recorded = await recordResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)PaymentEntryKind.Payment, recorded.GetProperty("kind").GetInt32());

        var reversalResponse = await client.PostAsJsonAsync($"/payments/{entryId}/reversal", new
        {
            reversalEntryId = Guid.NewGuid(),
            subjectKind = "Order",
            subjectId,
            actorId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.OK, reversalResponse.StatusCode);
        var reversal = await reversalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)PaymentEntryKind.Reversal, reversal.GetProperty("kind").GetInt32());

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var countCmd = new NpgsqlCommand("SELECT count(*) FROM payment_entries WHERE subject_id = $1", owner);
        countCmd.Parameters.AddWithValue(subjectId);
        var count = (long)countCmd.ExecuteScalar()!;
        Assert.Equal(2, count); // original + reversal — never a DELETE
    }

    [Fact]
    public async Task GetSettlement_ReportsOwedAndSettled_AndIsolatesUnrelatedOrders()
    {
        if (!_postgresAvailable)
        {
            Console.WriteLine("SKIPPED: no live Postgres. Start deploy/dev/compose.yaml.");
            return;
        }

        using var approvedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseSetting("Payments:ManuallyRecordedApprovalEnabled", "true");
        });

        var (client, _) = await SignedInClientAsync(approvedFactory);
        var unsettledOrderId = Guid.NewGuid();
        var unrelatedOrderId = Guid.NewGuid();

        await client.PostAsJsonAsync("/payments/", new
        {
            entryId = Guid.NewGuid(),
            subjectKind = "Order",
            subjectId = unsettledOrderId,
            method = "Cash",
            amount = 40m,
            actorId = Guid.NewGuid(),
        });

        var unsettledResponse = await client.GetAsync($"/payments/orders/{unsettledOrderId}/settlement?target=100");
        Assert.Equal(HttpStatusCode.OK, unsettledResponse.StatusCode);
        var unsettled = await unsettledResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(40m, unsettled.GetProperty("settled").GetDecimal());
        Assert.Equal(60m, unsettled.GetProperty("outstanding").GetDecimal());

        // A prior order's unsettled balance does not affect an unrelated one.
        var unrelatedResponse = await client.GetAsync($"/payments/orders/{unrelatedOrderId}/settlement?target=50");
        var unrelated = await unrelatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0m, unrelated.GetProperty("settled").GetDecimal());
        Assert.Equal(50m, unrelated.GetProperty("outstanding").GetDecimal());
    }
}
