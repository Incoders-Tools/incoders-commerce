using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Commerce.Integration.StockTestSeed;

namespace Commerce.Integration;

/// <summary>
/// purchases-receptions-and-stock T5, cloud side: `GET /device/stock/sync` (cursor/replica channel `stock`). The device
/// bearer fixes org AND branch; the response carries the DERIVED on-hand (SUM of the movements) of every presentation of
/// that branch with a movement since the cursor (minus a grace window, see <see cref="PostgresStockStore"/>).
/// </summary>
[Collection("Postgres")]
public sealed class StockReplicaSyncTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public StockReplicaSyncTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (!_postgresAvailable) return;
        using var owner = OpenOwner();
        var dir = Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, Path.GetFileName(file));
        }
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid Org, Guid Branch, string Token)> NewDeviceAsync(Guid? org = null)
    {
        var orgId = org ?? Guid.NewGuid();
        Guid branchId;
        using (var owner = OpenOwner())
        {
            if (org is null) Exec(owner, "INSERT INTO organizations (id, name) VALUES ($1, 'Org')", orgId);
            branchId = Branch(owner, orgId);
        }
        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(orgId), Guid.NewGuid(), branchId, Guid.NewGuid(), CancellationToken.None);
        return (orgId, branchId, issued.PlaintextToken);
    }

    private static void Move(Guid org, Guid branch, Guid presentation, decimal quantity, string kind = "Opening", TimeSpan? age = null)
    {
        using var owner = OpenOwner();
        Exec(owner,
            """
            INSERT INTO stock_movements (id, organization_id, branch_id, presentation_id, quantity, kind, occurred_at_utc, created_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, now(), now() - make_interval(secs => $7))
            """, Guid.NewGuid(), org, branch, presentation, quantity, kind, (age ?? TimeSpan.Zero).TotalSeconds);
    }

    private async Task<StockSyncResponse> PullAsync(string? token, DateTimeOffset since)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/device/stock/sync?since={Uri.EscapeDataString(since.ToString("O"))}");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StockSyncResponse>())!;
    }

    [Fact]
    public async Task Sync_WithoutADeviceBearer_Is401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var response = await _factory.CreateClient().GetAsync($"/device/stock/sync?since={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FirstPull_CarriesTheDerivedOnHandOfTheDeviceBranch_AndNothingOfOtherBranches()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, token) = await NewDeviceAsync();
        var meat = Presentation(org, branch);
        var sausage = Presentation(org, branch, "Chorizo", "Unidad", "FixedQuantity");
        Move(org, branch, meat, 120m);
        Move(org, branch, meat, -2.5m, "Sale");
        Move(org, branch, sausage, 10m);
        var otherBranch = Branch(OpenOwner(), org, "Otra sucursal");
        var foreign = Presentation(org, otherBranch);
        Move(org, otherBranch, foreign, 99m);

        var body = await PullAsync(token, DateTimeOffset.MinValue);

        Assert.Equal(117.5m, Assert.Single(body.Items, i => i.PresentationId == meat).OnHand);
        Assert.Equal(10m, Assert.Single(body.Items, i => i.PresentationId == sausage).OnHand);
        Assert.DoesNotContain(body.Items, i => i.PresentationId == foreign);
        Assert.True(body.ServerTimeUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task IncrementalPull_OnlyCarriesPresentationsWithANewMovement_WithTheirFullOnHand()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, token) = await NewDeviceAsync();
        var untouched = Presentation(org, branch);
        var touched = Presentation(org, branch, "Chorizo", "Unidad", "FixedQuantity");
        Move(org, branch, untouched, 5m, age: TimeSpan.FromHours(2));
        Move(org, branch, touched, 8m, age: TimeSpan.FromHours(2));
        var cursor = DateTimeOffset.UtcNow;
        Move(org, branch, touched, -3m, "Sale");

        var body = await PullAsync(token, cursor);

        var item = Assert.Single(body.Items);
        Assert.Equal(touched, item.PresentationId);
        Assert.Equal(5m, item.OnHand); // the absolute snapshot, not the delta
    }

    [Fact]
    public async Task IncrementalPull_ReplaysMovementsCommittedJustBeforeTheCursor_WithinTheGraceWindow()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var (org, branch, token) = await NewDeviceAsync();
        var presentation = Presentation(org, branch);
        // created_at_utc is the START of the writing transaction: a slow commit lands after a cursor taken meanwhile.
        Move(org, branch, presentation, 7m, age: TimeSpan.FromSeconds(30));

        var body = await PullAsync(token, DateTimeOffset.UtcNow);

        Assert.Equal(7m, Assert.Single(body.Items).OnHand);
    }

    [Fact]
    public void DevInitSnapshot_CarriesTheStockReplicaMigrationVerbatim()
    {
        static string Lf(string s) => s.Replace("\r\n", "\n");
        var root = PostgresTestFixture.RepoRoot();
        var init = Lf(File.ReadAllText(Path.Combine(root, "deploy", "dev", "db", "init-rls.sql")));
        Assert.Contains(Lf(File.ReadAllText(Path.Combine(root, "deploy", "db", "migrations", "0034_stock_replica_index.sql"))), init);
    }
}
