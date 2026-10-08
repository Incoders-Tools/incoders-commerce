using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// admin-console-field-fixes T5 (server): desktop management without a password. The customer and staff admin
/// endpoints the POS uses accept the paired device credential plus <c>X-Operator-Id</c>, authorized only when that
/// operator is in the device's organization, not revoked, holds <c>ManageUsers</c> (from the store) and has the
/// device's branch in scope; every other device call to them is refused with the same 403. Endpoints that did not
/// opt in keep refusing a device credential exactly as before, and the browser cookie path is unchanged.
/// </summary>
[Collection("Postgres")]
public sealed class DeviceOperatorManagementTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";
    private const string OperatorHeader = "X-Operator-Id";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public DeviceOperatorManagementTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (_postgresAvailable) ApplyMigrations();
    }

    public void Dispose() => _factory.Dispose();

    private static void ApplyMigrations()
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.Ordinal))
        {
            PostgresTestFixture.ApplyMigration(owner, file);
        }
    }

    private static object? OwnerScalar(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return cmd.ExecuteScalar();
    }

    private static void OwnerExec(string sql, params object[] args)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(sql, owner);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.ExecuteNonQuery();
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    private sealed record Tenant(Guid OrganizationId, Guid BranchId, Guid AdminId, string AdminEmail);

    private async Task<Tenant> BootstrapAsync(string prefix)
    {
        var email = Unique(prefix);
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap", new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", email, Password));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<BootstrapResponse>())!;
        return new Tenant(organizationId, body.BranchId, body.UserId, email);
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
    });

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = NewClient();
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<string> PairDeviceAsync(Tenant tenant, Guid? branchId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>().IssueAsync(
            new CloudTenantScope(tenant.OrganizationId), Guid.NewGuid(), branchId ?? tenant.BranchId, tenant.AdminId, CancellationToken.None);
        return issued.PlaintextToken;
    }

    /// <summary>A client carrying only the device credential and, when given, the operator header (no cookie jar).</summary>
    private HttpClient DeviceClient(string deviceToken, string? operatorId)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        if (operatorId is not null) client.DefaultRequestHeaders.Add(OperatorHeader, operatorId);
        return client;
    }

    private async Task<Guid> CreateStaffAsync(HttpClient admin, string role, Guid branchId)
    {
        var response = await admin.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("dop-staff"), Password, [role], [branchId]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateUserResponse>())!.UserId;
    }

    private static async Task<Guid> CreateBranchAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/account/branches", new CreateBranchRequest("Otra " + Guid.NewGuid().ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateBranchResponse>())!.BranchId;
    }

    /// <summary>Leaves the user scoped only to <paramref name="branchId"/> (the admin cannot assign a branch it lacks).</summary>
    private static void MoveToBranch(Guid userId, Guid branchId) =>
        OwnerExec("UPDATE users SET branch_scope = ARRAY[$1]::uuid[] WHERE id = $2", branchId, userId);

    private static object NewCustomerBody(string name) => new
    {
        customerKind = "Retail", displayName = name, taxIdType = "None", taxCondition = "ConsumidorFinal",
        partyType = "Person",
    };

    // ------------------------------------------------------------------
    // Positive path
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeviceWithAnAdminOperator_ManagesCustomersAndStaff_AndActsAsThatOperator()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-ok");
        var device = DeviceClient(await PairDeviceAsync(tenant), tenant.AdminId.ToString());

        // Customers: list, create, update.
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/customers")).StatusCode);
        var created = await device.PostAsJsonAsync("/customers", NewCustomerBody("Cliente desde terminal"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var customerId = (await created.Content.ReadFromJsonAsync<CreateCustomerResponse>())!.CustomerId;
        var updated = await device.PutAsJsonAsync($"/customers/{customerId}", new
        {
            displayName = "Cliente editado", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(tenant.AdminId, (Guid)OwnerScalar(
            "SELECT actor_id FROM audit_log WHERE entity_id = $1 ORDER BY id DESC LIMIT 1", customerId)!);
        // One customer, fresh (the enable/disable row action re-reads it before writing).
        var fresh = await device.GetFromJsonAsync<JsonElement>($"/customers/{customerId}");
        Assert.Equal("Cliente editado", fresh.GetProperty("displayName").GetString());

        // Reference data of the customer form.
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/customers/business-types?includeInactive=true")).StatusCode);
        var provinces = await device.GetFromJsonAsync<JsonElement>("/geo/provinces");
        Assert.Equal(24, provinces.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/geo/cities?provinceId=82&limit=5")).StatusCode);

        // Staff: list, create (branch = the device's), roles, revoke/restore, reset password.
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/account/users")).StatusCode);
        var staffId = await CreateStaffAsync(device, RoleCatalog.Cashier, tenant.BranchId);
        Assert.Equal(tenant.AdminId, (Guid)OwnerScalar(
            "SELECT actor_id FROM audit_log WHERE entity_id = $1 AND action = 'user.created'", staffId)!);
        Assert.Equal("org-user", (string)OwnerScalar(
            "SELECT actor_kind FROM audit_log WHERE entity_id = $1 AND action = 'user.created'", staffId)!);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PutAsJsonAsync($"/account/users/{staffId}/roles", new AssignRolesRequest([RoleCatalog.Seller]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PutAsJsonAsync($"/account/users/{staffId}/status", new UpdateUserStatusRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PutAsJsonAsync($"/account/users/{staffId}/status", new UpdateUserStatusRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PostAsJsonAsync($"/account/users/{staffId}/reset-password", new AdminResetPasswordRequest("new-password-123"))).StatusCode);

        // The operator is the caller: it can still not revoke itself.
        var self = await device.PutAsJsonAsync($"/account/users/{tenant.AdminId}/status", new UpdateUserStatusRequest(true));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
    }

    /// <summary>
    /// Personal → Empleados at the POS (cross-layer parity with the web's Personal): the same <c>/employees</c> endpoints —
    /// list, create, edit, an advance from a treasury account, the account statement and Dar de baja / Reincorporar — plus
    /// the positions and the treasury accounts the forms pick from, every write acting as the operator.
    /// </summary>
    [Fact]
    public async Task DeviceWithAnAdminOperator_ManagesEmployees_LikeTheWeb()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-emp");
        var admin = await SignInAsync(tenant.AdminEmail);
        var role = await admin.PostAsJsonAsync("/employees/roles", new { name = "Carnicero" });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var safe = await admin.PostAsJsonAsync("/treasury/accounts", new CreateTreasuryAccountRequest("Safe", "Caja fuerte", tenant.BranchId, null));
        Assert.Equal(HttpStatusCode.Created, safe.StatusCode);
        var accountId = (await safe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountId").GetGuid();

        var device = DeviceClient(await PairDeviceAsync(tenant), tenant.AdminId.ToString());

        var roles = await device.GetFromJsonAsync<JsonElement>("/employees/roles?includeInactive=true");
        Assert.Contains(roles.EnumerateArray(), r => r.GetProperty("id").GetGuid() == roleId);
        var accounts = await device.GetFromJsonAsync<JsonElement>("/treasury/accounts");
        Assert.Contains(accounts.EnumerateArray(), a => a.GetProperty("accountId").GetGuid() == accountId);

        var created = await device.PostAsJsonAsync("/employees", new EmployeeRequest(
            tenant.BranchId, null, "Ana", "Pérez", "30111222", null, roleId, null, null, null, new DateOnly(2024, 3, 15),
            "Monthly", 650_000m, null, true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var employee = await created.Content.ReadFromJsonAsync<JsonElement>();
        var employeeId = employee.GetProperty("id").GetGuid();
        Assert.Equal("Pérez, Ana", employee.GetProperty("fullName").GetString());
        Assert.NotEqual(JsonValueKind.Null, employee.GetProperty("customerId").ValueKind);
        Assert.Equal(tenant.AdminId, (Guid)OwnerScalar(
            "SELECT actor_id FROM audit_log WHERE entity_id = $1 ORDER BY id LIMIT 1", employeeId)!);

        var updated = await device.PutAsJsonAsync($"/employees/{employeeId}", new EmployeeRequest(
            tenant.BranchId, employee.GetProperty("fileNumber").GetInt32(), "Ana", "Pérez", "30111222", null, roleId, "2901 444555",
            null, null, new DateOnly(2024, 3, 15), "Biweekly", 320_000m, null, true));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var advance = await device.PostAsJsonAsync($"/employees/{employeeId}/advances", new EmployeeAdvanceRequest(50_000m, null, accountId, "A cuenta"));
        Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
        var statement = await device.GetFromJsonAsync<JsonElement>($"/employees/{employeeId}/account/statement");
        Assert.Equal(-50_000m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync($"/employees/{employeeId}/account/summary")).StatusCode);

        var listed = await device.GetFromJsonAsync<JsonElement>($"/employees?branchId={tenant.BranchId}&includeInactive=true");
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal((-50_000m, "Biweekly"), (row.GetProperty("balance").GetDecimal(), row.GetProperty("payFrequency").GetString()));

        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PostAsJsonAsync($"/employees/{employeeId}/active", new SetEmployeeActiveRequest(false, null))).StatusCode);
        Assert.False((bool)OwnerScalar("SELECT is_active FROM employees WHERE id = $1", employeeId)!);
        Assert.Equal(HttpStatusCode.NoContent,
            (await device.PostAsJsonAsync($"/employees/{employeeId}/active", new SetEmployeeActiveRequest(true, null))).StatusCode);

        // Manual movements on the employee's account stay a web (accounting) operation.
        var manual = await device.PostAsJsonAsync($"/employees/{employeeId}/account/movements",
            new { kind = "Adjustment", direction = "Credit", amount = 1m, concept = "No" });
        Assert.False(manual.IsSuccessStatusCode);
    }

    [Fact]
    public async Task DeviceOperator_KeepsTheGrantCap_AndTheBranchRule()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-cap");
        var admin = await SignInAsync(tenant.AdminEmail);

        // A manager who may manage users but holds less than a business admin.
        var managerId = await CreateStaffAsync(admin, RoleCatalog.Cashier, tenant.BranchId);
        OwnerExec("UPDATE users SET roles = $1::jsonb WHERE id = $2",
            JsonSerializer.Serialize(new[] { new { name = "manager", permissions = (int)(Permission.ManageUsers | Permission.OperatePos) } }),
            managerId);
        var device = DeviceClient(await PairDeviceAsync(tenant), managerId.ToString());

        var tooMuch = await device.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("dop-cap-new"), Password, [RoleCatalog.BusinessAdmin], [tenant.BranchId]));
        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);

        var cashierId = await CreateStaffAsync(device, RoleCatalog.Cashier, tenant.BranchId);
        var promote = await device.PutAsJsonAsync($"/account/users/{cashierId}/roles", new AssignRolesRequest([RoleCatalog.BusinessAdmin]));
        Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

        // A terminal only assigns its own branch (T6): any other branch is a validation problem.
        var otherBranch = await CreateBranchAsync(admin);
        var outside = await device.PostAsJsonAsync("/account/users",
            new CreateUserRequest(Unique("dop-cap-out"), Password, [RoleCatalog.Cashier], [otherBranch]));
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
    }

    // ------------------------------------------------------------------
    // Refusals: every failing operator answers the same 403
    // ------------------------------------------------------------------

    public enum Refusal
    {
        NoHeader,
        MalformedHeader,
        UnknownUser,
        OtherOrganization,
        Revoked,
        NoManageUsers,
        BranchOutOfScope,
    }

    [Theory]
    [InlineData(Refusal.NoHeader)]
    [InlineData(Refusal.MalformedHeader)]
    [InlineData(Refusal.UnknownUser)]
    [InlineData(Refusal.OtherOrganization)]
    [InlineData(Refusal.Revoked)]
    [InlineData(Refusal.NoManageUsers)]
    [InlineData(Refusal.BranchOutOfScope)]
    public async Task DeviceCall_WithAFailingOperator_IsRefusedWithTheSame403_OnEveryOptedInEndpoint(Refusal refusal)
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-no");
        var admin = await SignInAsync(tenant.AdminEmail);

        string? operatorId;
        switch (refusal)
        {
            case Refusal.NoHeader:
                operatorId = null;
                break;
            case Refusal.MalformedHeader:
                operatorId = "not-a-guid";
                break;
            case Refusal.UnknownUser:
                operatorId = Guid.NewGuid().ToString();
                break;
            case Refusal.OtherOrganization:
                operatorId = (await BootstrapAsync("dop-no-other")).AdminId.ToString();
                break;
            case Refusal.Revoked:
                var revokedId = await CreateStaffAsync(admin, RoleCatalog.BusinessAdmin, tenant.BranchId);
                Assert.Equal(HttpStatusCode.NoContent,
                    (await admin.PutAsJsonAsync($"/account/users/{revokedId}/status", new UpdateUserStatusRequest(true))).StatusCode);
                operatorId = revokedId.ToString();
                break;
            case Refusal.NoManageUsers:
                operatorId = (await CreateStaffAsync(admin, RoleCatalog.Cashier, tenant.BranchId)).ToString();
                break;
            case Refusal.BranchOutOfScope:
                var otherBranch = await CreateBranchAsync(admin);
                var elsewhereId = await CreateStaffAsync(admin, RoleCatalog.BusinessAdmin, tenant.BranchId);
                MoveToBranch(elsewhereId, otherBranch);
                operatorId = elsewhereId.ToString();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(refusal));
        }

        var device = DeviceClient(await PairDeviceAsync(tenant), operatorId);
        var existingUser = tenant.AdminId;
        var responses = new[]
        {
            await device.GetAsync("/customers"),
            await device.GetAsync($"/customers/{Guid.NewGuid()}"),
            await device.PostAsJsonAsync("/customers", NewCustomerBody("No")),
            await device.PutAsJsonAsync($"/customers/{Guid.NewGuid()}", new { displayName = "No", taxIdType = "None", taxCondition = "ConsumidorFinal", isEnabled = true }),
            await device.GetAsync("/geo/provinces"),
            await device.GetAsync("/geo/cities"),
            await device.GetAsync("/account/users"),
            await device.PostAsJsonAsync("/account/users", new CreateUserRequest(Unique("dop-no-new"), Password, [RoleCatalog.Cashier], [tenant.BranchId])),
            await device.PutAsJsonAsync($"/account/users/{existingUser}/roles", new AssignRolesRequest([RoleCatalog.Cashier])),
            await device.PutAsJsonAsync($"/account/users/{existingUser}/status", new UpdateUserStatusRequest(true)),
            await device.PostAsJsonAsync($"/account/users/{existingUser}/reset-password", new AdminResetPasswordRequest("x-password-123")),
            await device.GetAsync("/employees"),
            await device.GetAsync($"/employees/{Guid.NewGuid()}"),
            await device.PostAsJsonAsync("/employees", new { branchId = tenant.BranchId, firstName = "No", lastName = "No" }),
            await device.PostAsJsonAsync($"/employees/{Guid.NewGuid()}/active", new { isActive = false }),
            await device.PostAsJsonAsync($"/employees/{Guid.NewGuid()}/advances", new { amount = 1m, accountId = Guid.NewGuid() }),
            await device.GetAsync($"/employees/{Guid.NewGuid()}/account/statement"),
            await device.GetAsync("/employees/roles"),
            await device.GetAsync("/customers/business-types"),
            await device.GetAsync("/treasury/accounts"),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            // One body for every reason: it says the operator was refused, never why.
            Assert.Equal("""{"error":"operator-not-authorized"}""", await response.Content.ReadAsStringAsync());
        }

        // Nothing was written by the refused calls.
        Assert.Equal(0L, (long)OwnerScalar("SELECT count(*) FROM customers WHERE organization_id = $1", tenant.OrganizationId)!);
        Assert.False((bool)OwnerScalar("SELECT is_revoked FROM users WHERE id = $1", tenant.AdminId)!);
    }

    [Fact]
    public async Task UnknownDeviceCredential_OnAnOptedInEndpoint_IsUnauthorized()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-unknown");
        var device = DeviceClient("not-a-real-device-token", tenant.AdminId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/customers")).StatusCode);
    }

    // ------------------------------------------------------------------
    // No widening: endpoints that did not opt in refuse a device as before
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/orders/pending")]
    [InlineData("GET", "/account/me")]
    [InlineData("GET", "/account/organization/settings")]
    [InlineData("GET", "/account/branches")]
    [InlineData("GET", "/catalog/products")]
    [InlineData("PUT", "/account/users/{admin}/branches")]
    [InlineData("POST", "/customers/{customer}/ordering-access")]
    [InlineData("POST", "/geo/cities")]
    [InlineData("PUT", "/geo/cities/{customer}")]
    [InlineData("GET", "/payroll/runs")]
    [InlineData("POST", "/employees/roles")]
    [InlineData("POST", "/customers/business-types")]
    [InlineData("POST", "/treasury/accounts")]
    [InlineData("POST", "/employees/{customer}/account/movements")]
    public async Task DeviceCredential_OnAnEndpointThatDidNotOptIn_IsRefusedLikeAnAnonymousCall(string method, string pathTemplate)
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-wide");
        var path = pathTemplate.Replace("{admin}", tenant.AdminId.ToString()).Replace("{customer}", Guid.NewGuid().ToString());
        var token = await PairDeviceAsync(tenant);

        async Task<HttpResponseMessage> SendAsync(HttpClient client)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = JsonContent.Create(new { name = "x", provinceId = "82", branchIds = new[] { tenant.BranchId }, credential = Guid.NewGuid() });
            return await client.SendAsync(request);
        }

        var anonymous = await SendAsync(NewClient());
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.False(anonymous.IsSuccessStatusCode);

        foreach (var operatorId in new string?[] { null, tenant.AdminId.ToString() })
        {
            var device = await SendAsync(DeviceClient(token, operatorId));
            Assert.Equal(anonymous.StatusCode, device.StatusCode);
            Assert.Equal(anonymous.Headers.Location, device.Headers.Location);
        }
    }

    [Fact]
    public async Task DeviceSyncEndpoints_KeepWorking_WithOrWithoutTheOperatorHeader()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-sync");
        var token = await PairDeviceAsync(tenant);
        var path = $"/device/customers/sync?since={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}";
        Assert.Equal(HttpStatusCode.OK, (await DeviceClient(token, null).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeviceClient(token, Guid.NewGuid().ToString()).GetAsync(path)).StatusCode);
    }

    // ------------------------------------------------------------------
    // Browser cookie path unchanged
    // ------------------------------------------------------------------

    [Fact]
    public async Task CookieCallers_AreUnchanged_AndTheOperatorHeaderIsIgnoredForThem()
    {
        if (!_postgresAvailable) return;
        var tenant = await BootstrapAsync("dop-cookie");
        var admin = await SignInAsync(tenant.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/account/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/geo/provinces")).StatusCode);

        // A cashier's cookie stays refused even when it names the admin as operator.
        var cashierEmail = Unique("dop-cookie-cashier");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/account/users",
            new CreateUserRequest(cashierEmail, Password, [RoleCatalog.Cashier], [tenant.BranchId]))).StatusCode);
        var cashier = await SignInAsync(cashierEmail);
        var plain = await cashier.GetAsync("/customers");
        cashier.DefaultRequestHeaders.Add(OperatorHeader, tenant.AdminId.ToString());
        var withHeader = await cashier.GetAsync("/customers");
        Assert.False(plain.IsSuccessStatusCode);
        Assert.Equal(plain.StatusCode, withHeader.StatusCode);
        Assert.False((await cashier.GetAsync("/account/users")).IsSuccessStatusCode);

        // The admin's cookie with a header naming the cashier still acts as the admin.
        admin.DefaultRequestHeaders.Add(OperatorHeader, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/customers")).StatusCode);

        // Anonymous stays anonymous.
        Assert.False((await NewClient().GetAsync("/customers")).IsSuccessStatusCode);
    }
}
