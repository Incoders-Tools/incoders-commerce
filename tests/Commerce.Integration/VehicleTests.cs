using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Vehicles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// vehicles V1: the business's vehicles as a lookup table of the organization: plate (unique, written any way, kept in
/// capitals without spaces or dashes), brand, model, year, notes and an active flag; listed by plate, managed by the
/// administration, read by whoever plans a delivery run.
/// </summary>
[Collection("Postgres")]
public sealed class VehicleTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Migration = "0057_vehicles.sql";
    private const string Password = "vehicles-password";
    private static readonly DateOnly Today = new(2026, 10, 13);

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource? _dataSource;

    public VehicleTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString)
                .UseSetting("ConnectionStrings:CommercePlatformRead", PostgresTestFixture.OwnerConnectionString));
        if (!_postgresAvailable) return;
        using (var owner = OpenOwner())
        {
            var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
            foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
            {
                PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
            }
        }

        _dataSource = NpgsqlDataSource.Create(PostgresTestFixture.DirectConnectionString);
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void APlate_IsKeptInCapitalsWithoutSpacesOrDashes_AndAVehicleReadsByItsPlateBrandAndModel()
    {
        Assert.Equal("AB123CD", VehicleRules.NormalizePlate(" ab 123-cd "));
        Assert.True(VehicleRules.IsValidPlate("AB123CD"));
        Assert.True(VehicleRules.IsValidPlate("ABC123"));
        Assert.False(VehicleRules.IsValidPlate(""));
        Assert.False(VehicleRules.IsValidPlate("A1"));
        Assert.False(VehicleRules.IsValidPlate("AB123CD9999"));
        Assert.False(VehicleRules.IsValidPlate("AB.123"));

        Assert.True(VehicleRules.IsValidYear(2027, Today));
        Assert.False(VehicleRules.IsValidYear(2028, Today));
        Assert.False(VehicleRules.IsValidYear(1949, Today));

        Assert.Equal("AB123CD · Ford F-4000", VehicleRules.Label("AB123CD", "Ford", "F-4000"));
        Assert.Equal("AB123CD · Ford", VehicleRules.Label("AB123CD", "Ford", null));
        Assert.Equal("AB123CD", VehicleRules.Label("AB123CD", null, " "));
    }

    [Fact]
    public void TheMigration_RerunsSafely_AndIsInTheDevSnapshot()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        using var owner = OpenOwner();
        PostgresTestFixture.ApplyMigration(owner, Migration);
        PostgresTestFixture.ApplyMigration(owner, Migration);
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'vehicles'"));
        Assert.Equal(1L, Scalar<long>(owner,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'delivery_runs' AND column_name = 'vehicle_id'"));

        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", Migration))),
            Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql"))));
    }

    [Fact]
    public async Task Vehicles_AreListedByPlate_OnePerPlate_AndADeactivatedOneOnlyWhenAsked()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, actor) = await SeedAsync();
        var scope = new CloudTenantScope(org, BranchId: branch);
        var store = new PostgresVehicleStore(_dataSource!);

        var (created, truck) = await store.CreateAsync(scope, actor, new VehicleInput("AB123CD", "Ford", "F-4000", 2015, "El camión", true), CancellationToken.None);
        Assert.Equal(VehicleWriteOutcome.Done, created);
        Assert.Equal("AB123CD · Ford F-4000", truck!.Label);
        await store.CreateAsync(scope, actor, new VehicleInput("AA000AA", "Fiat", "Fiorino", null, null, true), CancellationToken.None);
        Assert.Equal(VehicleWriteOutcome.PlateInUse,
            (await store.CreateAsync(scope, actor, new VehicleInput("AB123CD", null, null, null, null, true), CancellationToken.None)).Outcome);
        Assert.Equal(["AA000AA", "AB123CD"], (await store.ListAsync(scope, false, CancellationToken.None)).Select(v => v.Plate));

        var (updated, edited) = await store.UpdateAsync(scope, actor, truck.Id,
            new VehicleInput("AB123CD", "Ford", "F-4000", 2016, null, false), CancellationToken.None);
        Assert.Equal((VehicleWriteOutcome.Done, 2016, false), (updated, edited!.Year, edited.IsActive));
        Assert.Equal(["AA000AA"], (await store.ListAsync(scope, false, CancellationToken.None)).Select(v => v.Plate));
        Assert.Equal(2, (await store.ListAsync(scope, true, CancellationToken.None)).Count);
        Assert.Equal(VehicleWriteOutcome.NotFound,
            (await store.UpdateAsync(scope, actor, Guid.NewGuid(), new VehicleInput("ZZ999ZZ", null, null, null, null, true), CancellationToken.None)).Outcome);

        // Another organization neither sees them nor collides with their plates.
        var (other, otherBranch, otherActor) = await SeedAsync();
        var otherScope = new CloudTenantScope(other, BranchId: otherBranch);
        Assert.Empty(await store.ListAsync(otherScope, true, CancellationToken.None));
        Assert.Equal(VehicleWriteOutcome.Done,
            (await store.CreateAsync(otherScope, otherActor, new VehicleInput("AB123CD", null, null, null, null, true), CancellationToken.None)).Outcome);
        using var owner = OpenOwner();
        Assert.Equal(2L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE organization_id = $1 AND action = 'vehicle.created'", org));
        Assert.Equal(1L, Scalar<long>(owner, "SELECT count(*) FROM audit_log WHERE organization_id = $1 AND action = 'vehicle.updated'", org));
    }

    [Fact]
    public async Task Vehicles_AreManagedByTheAdministration_AndReadByWhoeverPlansARun_OverHttp()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, _) = await SeedAsync();
        var admin = await SignedInAsync(org, branch, Permission.ManageUsers);
        var seller = await SignedInAsync(org, branch, Permission.TakeOrders);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/vehicles", new { plate = "A-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/vehicles", new { plate = "AB123CD", year = 1900 })).StatusCode);
        var created = await admin.PostAsJsonAsync("/vehicles", new { plate = "ab 123 cd", brand = "Ford", model = "F-4000", year = 2015 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var truck = await Json(created);
        Assert.Equal(("AB123CD", true), (truck.GetProperty("plate").GetString(), truck.GetProperty("isActive").GetBoolean()));
        var again = await admin.PostAsJsonAsync("/vehicles", new { plate = "AB-123-CD" });
        Assert.Equal("vehicle-plate-in-use", (await Json(again)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync("/vehicles", new { plate = "AA000AA" })).StatusCode);
        var listed = (await Json(await seller.GetAsync("/vehicles"))).EnumerateArray().ToList();
        Assert.Equal("AB123CD · Ford F-4000", Assert.Single(listed).GetProperty("label").GetString());

        var id = truck.GetProperty("id").GetGuid();
        var edited = await admin.PutAsJsonAsync($"/vehicles/{id}", new { plate = "AB123CD", brand = "Ford", model = "F-4000", year = 2015, isActive = false });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Empty((await Json(await seller.GetAsync("/vehicles"))).EnumerateArray());
        Assert.Single((await Json(await admin.GetAsync("/vehicles?includeInactive=true"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/vehicles/{Guid.NewGuid()}", new { plate = "AA000AA" })).StatusCode);
    }

    private async Task<(Guid Org, Guid Branch, Guid Actor)> SeedAsync()
    {
        var userStore = new PostgresUserAccountStore(_dataSource!);
        var orgStore = new PostgresOrganizationStore(_dataSource!, userStore);
        var (org, branch, actor) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapOutcome.Created, await orgStore.TryCreateBootstrapAsync(
            new CloudTenantScope(org), new NewOrganization(org, "Vehicles Org"), new NewBranch(branch, "Arrecifes"),
            new NewUserAccount(actor, $"vehicles-{org}@example.com", "hash", [branch], [new RoleDto("admin", Permission.ManageUsers)]),
            CancellationToken.None));
        return (org, branch, actor);
    }

    private async Task<HttpClient> SignedInAsync(Guid org, Guid branch, Permission permissions)
    {
        var userId = Guid.NewGuid();
        var email = $"{userId}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<PostgresUserAccountStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.PasswordHasher<UserAccount>>();
            var user = new NewUserAccount(
                userId, email, hasher.HashPassword(new UserAccount(userId, org, [], []), Password), new[] { branch },
                new[] { new RoleDto("test-role", permissions) });
            Assert.Equal(CreateStaffUserOutcome.Created, await store.CreateStaffUserAsync(
                new CloudTenantScope(org), user,
                new Commerce.Cloud.Api.Auditing.UserManagementAuditEntry("org-user", Guid.NewGuid(), org, "user", userId, "user.created", null, null),
                CancellationToken.None));
        }

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true, AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"),
        });
        (await client.PostAsJsonAsync("/account/sign-in", new SignInRequest(email, Password))).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add(TenantScopeEndpointFilter.BranchSelectorHeader, branch.ToString());
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
