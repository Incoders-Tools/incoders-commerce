using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Covers commerce-pos-installation-identity task 3.5 (the full
/// `/device/pair` matrix) and task 4.4 (the breaking-change regression
/// guard: the old bearer shape, an unissued token, and a revoked token all
/// fail `/sync/inbox`, and a valid token's persisted `sync_inbox` row carries
/// the SERVER's identity, never the caller's).
/// </summary>
[Collection("Postgres")]
public sealed class DeviceEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public DeviceEndpointTests(WebApplicationFactory<Program> factory)
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
            throw new InvalidOperationException("Could not locate repo root.");
        }
        return dir.FullName;
    }

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();

        var repoRoot = RepoRoot();

        var initSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0001_init_rls.sql"))
            .Replace("__APP_RUNTIME_PASSWORD__", "dev-only-password");
        using (var cmd = new NpgsqlCommand(initSql, owner)) cmd.ExecuteNonQuery();

        var usersSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0002_users.sql"));
        using (var cmd = new NpgsqlCommand(usersSql, owner)) cmd.ExecuteNonQuery();

        var orgsSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0003_organizations_branches.sql"));
        using (var cmd = new NpgsqlCommand(orgsSql, owner)) cmd.ExecuteNonQuery();

        var deviceSql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0004_device_credentials.sql"));
        using (var cmd = new NpgsqlCommand(deviceSql, owner)) cmd.ExecuteNonQuery();

        var recoverySql = File.ReadAllText(Path.Combine(repoRoot, "deploy", "db", "migrations", "0005_password_recovery.sql"));
        using (var cmd = new NpgsqlCommand(recoverySql, owner)) cmd.ExecuteNonQuery();

        // CASCADE covers `customers` (0008), which may already exist in this
        // shared database from another test class in the same run even
        // though this class never applies 0008 itself.
        using var resetCmd = new NpgsqlCommand(
            "TRUNCATE TABLE password_reset_tokens, sync_inbox, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        resetCmd.ExecuteNonQuery();
    }

    private async Task<(Guid OrgId, Guid BranchId, Guid UserId)> SeedSingleBranchOperatorAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();

        var orgId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, orgId, [], []), password);

        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId),
            new NewOrganization(orgId, "Single Branch Co"),
            new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, email, passwordHash, [branchId], [new RoleDto("cashier", Permission.ViewSales)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);

        return (orgId, branchId, userId);
    }

    private async Task<(Guid OrgId, Guid BranchAId, Guid BranchBId, Guid UserId)> SeedMultiBranchOperatorAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var orgStore = scope.ServiceProvider.GetRequiredService<PostgresOrganizationStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();

        var orgId = Guid.NewGuid();
        var branchAId = Guid.NewGuid();
        var branchBId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, orgId, [], []), password);

        var outcome = await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(orgId),
            new NewOrganization(orgId, "Multi Branch Co"),
            new NewBranch(branchAId, "Downtown"),
            new NewUserAccount(userId, email, passwordHash, [branchAId, branchBId], [new RoleDto("cashier", Permission.ViewSales)]),
            CancellationToken.None);
        Assert.Equal(BootstrapOutcome.Created, outcome);

        // Second branch, added directly (bootstrap only creates one).
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var insertBranchCmd = new NpgsqlCommand(
            "INSERT INTO branches (id, organization_id, name) VALUES ($1, $2, 'Uptown')", owner);
        insertBranchCmd.Parameters.AddWithValue(branchBId);
        insertBranchCmd.Parameters.AddWithValue(orgId);
        insertBranchCmd.ExecuteNonQuery();

        return (orgId, branchAId, branchBId, userId);
    }

    private async Task<(Guid OrgId, Guid UserId)> SeedZeroBranchOperatorAsync(string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, orgId, [], []), password);

        // No org/branch exists yet for a zero-branch user; user_directory
        // requires an organization row via FK-free design, but users table
        // has no FK either — insert org first purely so RLS org scope works.
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var insertOrgCmd = new NpgsqlCommand("INSERT INTO organizations (id, name) VALUES ($1, 'Zero Branch Co')", owner);
        insertOrgCmd.Parameters.AddWithValue(orgId);
        insertOrgCmd.ExecuteNonQuery();

        var created = await userStore.TryCreateAsync(
            new CloudTenantScope(orgId),
            new NewUserAccount(userId, email, passwordHash, [], [new RoleDto("cashier", Permission.ViewSales)]),
            CancellationToken.None);
        Assert.True(created);

        return (orgId, userId);
    }

    [Fact]
    public async Task Pair_WrongPassword_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await SeedSingleBranchOperatorAsync("wrongpass-device@example.com", "correct-password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("wrongpass-device@example.com", "incorrect-password", Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pair_UnknownEmail_Returns401_SameShapeAsWrongPassword()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("nobody-device@example.com", "whatever", Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pair_ZeroBranches_Returns403_NoBranchesAssigned_NotGeneric401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await SeedZeroBranchOperatorAsync("zero-branch@example.com", "some-password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("zero-branch@example.com", "some-password", Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.Equal("no-branches-assigned", body!.Status);
    }

    [Fact]
    public async Task Pair_SingleBranch_AutoPairs_WithToken()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, _) = await SeedSingleBranchOperatorAsync("single-branch@example.com", "some-password");
        var installationId = Guid.NewGuid();

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("single-branch@example.com", "some-password", installationId, null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.Equal("paired", body!.Status);
        Assert.Equal(orgId, body.OrganizationId);
        Assert.Equal(branchId, body.BranchId);
        Assert.Equal(installationId, body.InstallationId);
        Assert.False(string.IsNullOrEmpty(body.DeviceToken));
    }

    [Fact]
    public async Task Pair_MultiBranch_NoBranchIdGiven_ReturnsSelectionRequired_ListingOnlyInScopeBranches()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (_, branchAId, branchBId, _) = await SeedMultiBranchOperatorAsync("multi-branch@example.com", "some-password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("multi-branch@example.com", "some-password", Guid.NewGuid(), null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.Equal("branch-selection-required", body!.Status);
        Assert.Null(body.DeviceToken);
        Assert.NotNull(body.Branches);
        Assert.Equal(2, body.Branches!.Count);
        Assert.Contains(body.Branches, b => b.Id == branchAId);
        Assert.Contains(body.Branches, b => b.Id == branchBId);
    }

    [Fact]
    public async Task Pair_MultiBranch_StepTwo_RepostsCredentialsWithChosenBranch_Pairs()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchAId, _, _) = await SeedMultiBranchOperatorAsync("multi-step2@example.com", "some-password");
        var installationId = Guid.NewGuid();

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("multi-step2@example.com", "some-password", installationId, branchAId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.Equal("paired", body!.Status);
        Assert.Equal(branchAId, body.BranchId);
        Assert.False(string.IsNullOrEmpty(body.DeviceToken));
    }

    [Fact]
    public async Task Pair_BranchIdOutsideScope_Returns403_BranchNotInScope()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await SeedSingleBranchOperatorAsync("outside-scope@example.com", "some-password");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("outside-scope@example.com", "some-password", Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.Equal("branch-not-in-scope", body!.Status);
    }

    [Fact]
    public async Task Pair_TokenNeverAppearsInAnyOtherResponse()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        await SeedSingleBranchOperatorAsync("token-once@example.com", "some-password");

        var client = _factory.CreateClient();
        var pairResponse = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("token-once@example.com", "some-password", Guid.NewGuid(), null));
        var pairedBody = await pairResponse.Content.ReadFromJsonAsync<DevicePairResponse>();
        Assert.False(string.IsNullOrEmpty(pairedBody!.DeviceToken));

        // A wrong-password retry must never echo back or leak the prior token.
        var retryResponse = await client.PostAsJsonAsync("/device/pair",
            new DevicePairRequest("token-once@example.com", "wrong-password", Guid.NewGuid(), null));
        Assert.Equal(HttpStatusCode.Unauthorized, retryResponse.StatusCode);
        var retryBody = await retryResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(pairedBody.DeviceToken!, retryBody);
    }

    // --- The breaking-change regression guard (task 4.4) ---------------------

    [Fact]
    public async Task Sync_OldBearerShape_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
        {
            Content = JsonContent.Create(SampleEnvelope(Guid.NewGuid(), Guid.NewGuid()))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{Guid.NewGuid()}.{Guid.NewGuid()}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_UnissuedRandomToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
        {
            Content = JsonContent.Create(SampleEnvelope(Guid.NewGuid(), Guid.NewGuid()))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "totally-unissued-random-token-value");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_RevokedToken_Returns401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedSingleBranchOperatorAsync("revoked-sync@example.com", "some-password");

        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(new CloudTenantScope(orgId), Guid.NewGuid(), branchId, userId, CancellationToken.None);
        await credentialStore.RevokeAsync(new CloudTenantScope(orgId), issued.Record.Id, CancellationToken.None);

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
        {
            Content = JsonContent.Create(SampleEnvelope(orgId, branchId))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sync_ValidToken_Returns200_AndPersistedRowCarriesServerIdentity_NeverCallerClaimed()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        var (orgId, branchId, userId) = await SeedSingleBranchOperatorAsync("valid-sync@example.com", "some-password");

        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(new CloudTenantScope(orgId), Guid.NewGuid(), branchId, userId, CancellationToken.None);

        // The envelope CLAIMS a different (bogus) organization/branch than
        // the credential's real row — the server must use ITS OWN identity,
        // never the caller's claimed values.
        var bogusOrgId = Guid.NewGuid();
        var bogusBranchId = Guid.NewGuid();
        var envelope = SampleEnvelope(bogusOrgId, bogusBranchId);

        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
        {
            Content = JsonContent.Create(envelope)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);

        var response = await client.SendAsync(request);

        // Defense in depth (CloudSyncReceiver/PostgresCloudInboxStore) denies
        // an envelope claiming an org other than the resolved scope — this
        // IS the proof that identity comes from the server, not the caller:
        // the mismatched envelope is denied rather than silently accepted
        // under the bogus org.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<InboundApplyResult>();
        Assert.Equal(InboundApplyOutcome.Denied, result!.Outcome);

        // Now send an envelope that correctly claims the CREDENTIAL's real
        // org/branch (as the real CloudSyncClient always does) — this
        // succeeds and the persisted row's org/branch match the credential's
        // server-side identity.
        var matchingEnvelope = SampleEnvelope(orgId, branchId);
        var matchingRequest = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox")
        {
            Content = JsonContent.Create(matchingEnvelope)
        };
        matchingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.PlaintextToken);

        var matchingResponse = await client.SendAsync(matchingRequest);
        Assert.Equal(HttpStatusCode.OK, matchingResponse.StatusCode);

        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT organization_id, branch_id FROM sync_inbox WHERE operation_id = $1", owner);
        cmd.Parameters.AddWithValue(matchingEnvelope.OperationId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(orgId, reader.GetGuid(0));
        Assert.Equal(branchId, reader.GetGuid(1));
    }

    private static SyncEnvelope SampleEnvelope(Guid organizationId, Guid branchId) => new(
        OperationId: Guid.NewGuid(),
        ContractVersion: 1,
        OrganizationId: organizationId,
        BranchId: branchId,
        AggregateId: Guid.NewGuid(),
        AggregateVersion: 1,
        ActorId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow,
        PayloadKind: "sale",
        Payload: "{\"v\":1}");
}
