using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// branch-discount-pin spec: set/rotate by administrators, hash-only storage,
/// audit, cross-branch and cross-tenant denial, and replication to the paired
/// terminals of THAT branch only.
/// </summary>
[Collection("Postgres")]
public sealed class BranchDiscountPinEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string OrganizationSelectorHeader = "X-Organization-Id";
    private const string Password = "correct-password";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public BranchDiscountPinEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (_postgresAvailable) ApplyMigrationsAndReset();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrationsAndReset()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }
        using var reset = new NpgsqlCommand(
            "TRUNCATE TABLE audit_log, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, Guid UserId)> BootstrapAsync(string email)
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        return (organizationId, body!.BranchId, body.UserId);
    }

    private async Task<HttpClient> SignInAsync(string email, Guid? actingOrganizationId = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        var response = await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password));
        response.EnsureSuccessStatusCode();
        if (actingOrganizationId is not null) client.DefaultRequestHeaders.Add(OrganizationSelectorHeader, actingOrganizationId.ToString());
        return client;
    }

    private static async Task<Guid> CreateBranchAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/account/branches", new CreateBranchRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateBranchResponse>())!.BranchId;
    }

    private async Task<string> CreateCashierAsync(Guid organizationId, Guid branchId)
    {
        var userId = Guid.NewGuid();
        var email = $"cashier-{userId:N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<UserAccount>>();
        var hash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), Password);
        var outcome = await store.CreateStaffUserAsync(
            new CloudTenantScope(organizationId),
            new NewUserAccount(userId, email, hash, [branchId], [new RoleDto("cashier", Permission.ViewSales)]),
            new UserManagementAuditEntry("org-user", Guid.NewGuid(), organizationId, "user", userId, "user.created", null, null),
            CancellationToken.None);
        Assert.Equal(CreateStaffUserOutcome.Created, outcome);
        return email;
    }

    private static void PromoteToSystemAdmin(Guid userId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var flag = new NpgsqlCommand("UPDATE users SET is_system_admin = true, roles = '[]'::jsonb, branch_scope = '{}' WHERE id = $1", owner);
        flag.Parameters.AddWithValue(userId);
        flag.ExecuteNonQuery();
    }

    private async Task<string> IssueDeviceTokenAsync(Guid organizationId, Guid branchId)
    {
        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(organizationId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return issued.PlaintextToken;
    }

    private async Task<HttpResponseMessage> DeviceGetAsync(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _factory.CreateClient().SendAsync(request);
    }

    private static Task<HttpResponseMessage> SetPinAsync(HttpClient client, Guid branchId, string pin) =>
        client.PutAsJsonAsync($"/account/branches/{branchId}/discount-pin", new { pin });

    private static (byte[] Salt, byte[] Hash, long Version)? ReadRow(Guid branchId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT salt, pin_hash, version FROM branch_discount_pins WHERE branch_id = $1", owner);
        cmd.Parameters.AddWithValue(branchId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ((byte[])reader[0], (byte[])reader[1], reader.GetInt64(2)) : null;
    }

    private static List<(Guid ActorId, Guid EntityId, string? OldValue, string? NewValue, string Action)> ReadAudit(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_id, entity_id, old_value::text, new_value::text, action FROM audit_log WHERE organization_id = $1 AND entity_type = 'branch-discount-pin' ORDER BY id", owner);
        cmd.Parameters.AddWithValue(organizationId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(Guid, Guid, string?, string?, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }
        return rows;
    }

    [Fact]
    public async Task Admin_SetsThePin_AndTheResponseNeverContainsIt()
    {
        if (!_postgresAvailable) return;
        var (_, branchId, _) = await BootstrapAsync("pin-admin@example.com");
        var admin = await SignInAsync("pin-admin@example.com");

        var before = await admin.GetFromJsonAsync<BranchDiscountPinStatus>($"/account/branches/{branchId}/discount-pin");
        Assert.False(before!.IsSet);
        Assert.Null(before.ChangedAtUtc);

        var response = await SetPinAsync(admin, branchId, "482913");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("482913", raw);
        var status = JsonSerializer.Deserialize<BranchDiscountPinStatus>(raw, Web)!;
        Assert.True(status.IsSet);
        Assert.Equal(1, status.Version);
        Assert.NotNull(status.ChangedAtUtc);

        var read = await admin.GetStringAsync($"/account/branches/{branchId}/discount-pin");
        Assert.DoesNotContain("482913", read);
        Assert.Contains("\"isSet\":true", read);
    }

    [Fact]
    public async Task Rotate_BumpsTheVersion_ChangesTheSalt_StoresOnlyAVerifier_AndAuditsTheActor()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, adminId) = await BootstrapAsync("pin-rotate@example.com");
        var admin = await SignInAsync("pin-rotate@example.com");

        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, branchId, "1357")).StatusCode);
        var first = ReadRow(branchId)!.Value;
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, branchId, "2468")).StatusCode);
        var second = ReadRow(branchId)!.Value;

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.NotEqual(first.Salt, second.Salt);
        var verifier = new DiscountPinVerifier(BranchDiscountPin.Pbkdf2Sha256, BranchDiscountPin.DefaultIterations, second.Salt, second.Hash);
        Assert.True(BranchDiscountPin.Verify("2468", verifier));
        Assert.False(BranchDiscountPin.Verify("1357", verifier));

        var audit = ReadAudit(organizationId);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, row =>
        {
            Assert.Equal(adminId, row.ActorId);
            Assert.Equal(branchId, row.EntityId);
            Assert.DoesNotContain("1357", (row.OldValue ?? "") + row.NewValue);
            Assert.DoesNotContain("2468", (row.OldValue ?? "") + row.NewValue);
        });
        Assert.Equal("branch.discount-pin.set", audit[0].Action);
        Assert.Equal("branch.discount-pin.rotated", audit[1].Action);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12a45")]
    [InlineData("")]
    [InlineData("1234567890123")]
    public async Task InvalidPin_IsRefused_AndChangesNothing(string pin)
    {
        if (!_postgresAvailable) return;
        var email = $"pin-invalid-{Guid.NewGuid():N}@example.com";
        var (_, branchId, _) = await BootstrapAsync(email);
        var admin = await SignInAsync(email);

        var response = await SetPinAsync(admin, branchId, pin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(ReadRow(branchId));
    }

    [Fact]
    public async Task Cashier_CannotSetOrReadTheStatus()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, _) = await BootstrapAsync("pin-cashier-admin@example.com");
        var cashier = await SignInAsync(await CreateCashierAsync(organizationId, branchId));

        Assert.Equal(HttpStatusCode.Forbidden, (await SetPinAsync(cashier, branchId, "1357")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/account/branches/{branchId}/discount-pin")).StatusCode);
        Assert.Null(ReadRow(branchId));
    }

    [Fact]
    public async Task AdminOfAnotherOrganization_CannotTouchThisBranch_AndItLooksMissing()
    {
        if (!_postgresAvailable) return;
        var (_, branchA, _) = await BootstrapAsync("pin-org-a@example.com");
        await BootstrapAsync("pin-org-b@example.com");
        var adminA = await SignInAsync("pin-org-a@example.com");
        var adminB = await SignInAsync("pin-org-b@example.com");
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(adminA, branchA, "1357")).StatusCode);
        var before = ReadRow(branchA)!.Value;

        Assert.Equal(HttpStatusCode.NotFound, (await SetPinAsync(adminB, branchA, "9999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/account/branches/{branchA}/discount-pin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetPinAsync(adminB, Guid.NewGuid(), "9999")).StatusCode);

        var after = ReadRow(branchA)!.Value;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Hash, after.Hash);
    }

    [Fact]
    public async Task SystemAdministratorActingOnAnOrganization_CanSet_AndIsTheAuditedActor()
    {
        if (!_postgresAvailable) return;
        var (targetOrg, targetBranch, _) = await BootstrapAsync("pin-target@example.com");
        var (_, _, sysadminId) = await BootstrapAsync("pin-sysadmin@example.com");
        PromoteToSystemAdmin(sysadminId);
        var sysadmin = await SignInAsync("pin-sysadmin@example.com", actingOrganizationId: targetOrg);

        var response = await SetPinAsync(sysadmin, targetBranch, "5555");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(sysadminId, Assert.Single(ReadAudit(targetOrg)).ActorId);
    }

    [Fact]
    public async Task Device_ReceivesOnlyItsOwnBranchVerifier_AndCanVerifyOffline()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, _) = await BootstrapAsync("pin-device@example.com");
        var admin = await SignInAsync("pin-device@example.com");
        var otherBranch = await CreateBranchAsync(admin, "Sucursal 2");
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, branchId, "1357")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, otherBranch, "8642")).StatusCode);

        var response = await DeviceGetAsync("/device/branch/discount-pin", await IssueDeviceTokenAsync(organizationId, branchId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("1357", raw);
        var dto = JsonSerializer.Deserialize<DeviceDiscountPinResponse>(raw, Web)!;
        Assert.True(dto.IsSet);
        Assert.Equal(1, dto.Version);
        var verifier = new DiscountPinVerifier(dto.Algorithm!, dto.Iterations!.Value, Convert.FromBase64String(dto.Salt!), Convert.FromBase64String(dto.Hash!));
        Assert.True(BranchDiscountPin.Verify("1357", verifier));
        Assert.False(BranchDiscountPin.Verify("8642", verifier));
    }

    [Fact]
    public async Task Device_OfABranchWithoutAPin_LearnsNoneIsSet_AndCannotAskForAnotherBranch()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, _) = await BootstrapAsync("pin-device-none@example.com");
        var admin = await SignInAsync("pin-device-none@example.com");
        var withPin = await CreateBranchAsync(admin, "Con PIN");
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, withPin, "8642")).StatusCode);
        var token = await IssueDeviceTokenAsync(organizationId, branchId);

        var response = await DeviceGetAsync($"/device/branch/discount-pin?branchId={withPin}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = JsonSerializer.Deserialize<DeviceDiscountPinResponse>(await response.Content.ReadAsStringAsync(), Web)!;
        Assert.False(dto.IsSet);
        Assert.Null(dto.Hash);
        Assert.Null(dto.Salt);
    }

    [Fact]
    public async Task Device_WithoutABearer_IsRejected()
    {
        if (!_postgresAvailable) return;
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeviceGetAsync("/device/branch/discount-pin", null)).StatusCode);
    }

    [Fact]
    public async Task RowLevelSecurity_HidesAPinFromEveryOtherBranchAndOrganizationScope()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, _) = await BootstrapAsync("pin-rls@example.com");
        var admin = await SignInAsync("pin-rls@example.com");
        var otherBranch = await CreateBranchAsync(admin, "Otra");
        var (foreignOrg, foreignBranch, _) = await BootstrapAsync("pin-rls-foreign@example.com");
        Assert.Equal(HttpStatusCode.OK, (await SetPinAsync(admin, branchId, "1357")).StatusCode);

        async Task<long> CountAsync(Guid? org, Guid? branch)
        {
            await using var connection = new NpgsqlConnection(PostgresTestFixture.DirectConnectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            if (org is not null)
            {
                await using var set = new NpgsqlCommand("SELECT set_config('app.current_org_id', $1, true)", connection, tx);
                set.Parameters.AddWithValue(org.Value.ToString());
                await set.ExecuteNonQueryAsync();
            }
            if (branch is not null)
            {
                await using var set = new NpgsqlCommand("SELECT set_config('app.current_branch_id', $1, true)", connection, tx);
                set.Parameters.AddWithValue(branch.Value.ToString());
                await set.ExecuteNonQueryAsync();
            }
            await using var count = new NpgsqlCommand("SELECT count(*) FROM branch_discount_pins", connection, tx);
            return (long)(await count.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await CountAsync(organizationId, branchId));
        Assert.Equal(0, await CountAsync(organizationId, otherBranch));
        Assert.Equal(0, await CountAsync(organizationId, null));
        Assert.Equal(0, await CountAsync(foreignOrg, branchId));
        Assert.Equal(0, await CountAsync(foreignOrg, foreignBranch));
        Assert.Equal(0, await CountAsync(null, null));
    }
}
