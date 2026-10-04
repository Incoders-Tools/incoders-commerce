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
/// operator-ux-adjustments T5, cloud side: `GET /device/organization/settings`. A device reads the quantity decimal
/// separator of ITS organization (the one of the stored device credential, never one named in the request), so the
/// terminal formats kilos the way the web settings say. Additive: no existing device route or payload changes.
/// </summary>
[Collection("Postgres")]
public sealed class DeviceOrganizationSettingsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Path = "/device/organization/settings";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public DeviceOrganizationSettingsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString));
        if (!_postgresAvailable) return;
        using var owner = OpenOwner();
        var dir = System.IO.Path.Combine(PostgresTestFixture.RepoRoot(), "deploy", "db", "migrations");
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(System.IO.Path.GetFileName))
        {
            PostgresTestFixture.ApplyMigration(owner, System.IO.Path.GetFileName(file));
        }
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid Org, string Token)> NewDeviceAsync(string separator)
    {
        var org = Guid.NewGuid();
        Guid branch;
        using (var owner = OpenOwner())
        {
            Exec(owner, "INSERT INTO organizations (id, name, quantity_decimal_separator) VALUES ($1, 'Org', $2)", org, separator);
            branch = Branch(owner, org);
        }

        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>()
            .IssueAsync(new CloudTenantScope(org), Guid.NewGuid(), branch, Guid.NewGuid(), CancellationToken.None);
        return (org, issued.PlaintextToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task WithoutADeviceBearer_Is401()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync("not-a-real-device-token")).StatusCode);
    }

    [Fact]
    public async Task EachDevice_ReadsTheSeparatorOfItsOwnOrganization()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var dot = await NewDeviceAsync("Dot");
        var comma = await NewDeviceAsync("Comma");

        var dotResponse = await GetAsync(dot.Token);
        var commaResponse = await GetAsync(comma.Token);

        Assert.Equal(HttpStatusCode.OK, dotResponse.StatusCode);
        Assert.Equal("Dot", (await dotResponse.Content.ReadFromJsonAsync<DeviceOrganizationSettingsResponse>())!.QuantityDecimalSeparator);
        Assert.Equal("Comma", (await commaResponse.Content.ReadFromJsonAsync<DeviceOrganizationSettingsResponse>())!.QuantityDecimalSeparator);
    }

    [Fact]
    public async Task AChangeInTheWebSettings_IsWhatTheNextReadReturns()
    {
        if (!_postgresAvailable) { Console.WriteLine("SKIPPED: no live Postgres."); return; }
        var device = await NewDeviceAsync("Comma");
        using (var owner = OpenOwner())
        {
            Exec(owner, "UPDATE organizations SET quantity_decimal_separator = 'Dot' WHERE id = $1", device.Org);
        }

        var body = await (await GetAsync(device.Token)).Content.ReadFromJsonAsync<DeviceOrganizationSettingsResponse>();

        Assert.Equal("Dot", body!.QuantityDecimalSeparator);
    }
}
