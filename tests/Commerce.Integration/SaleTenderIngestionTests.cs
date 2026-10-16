using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Tender Recorded and Synchronized With the Sale", cloud
/// side: a sale with a tender is stored with it (the cloud keeps the payload as
/// received) and re-delivery stores nothing new; a payload with no tender still
/// ingests.
/// </summary>
[Collection("Postgres")]
public sealed class SaleTenderIngestionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public SaleTenderIngestionTests(WebApplicationFactory<Program> factory)
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
            "TRUNCATE TABLE audit_log, sync_inbox, customer_ordering_access, customers, password_reset_tokens, user_directory, users, device_credentials, branches, organizations CASCADE", owner);
        reset.ExecuteNonQuery();
    }

    private async Task<(Guid OrganizationId, Guid BranchId, string DeviceToken)> PairedTerminalAsync()
    {
        var organizationId = Guid.NewGuid();
        var token = _factory.Services.GetRequiredService<BootstrapTokenRegistry>().Issue(organizationId);
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/account/bootstrap",
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", $"tender-{organizationId:N}@example.com", Password));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();

        using var scope = _factory.Services.CreateScope();
        var credentialStore = scope.ServiceProvider.GetRequiredService<PostgresDeviceCredentialStore>();
        var issued = await credentialStore.IssueAsync(
            new CloudTenantScope(organizationId), Guid.NewGuid(), body!.BranchId, body.UserId, CancellationToken.None);
        return (organizationId, body.BranchId, issued.PlaintextToken);
    }

    private async Task<InboundApplyResult> PushAsync(string deviceToken, SyncEnvelope envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/sync/inbox") { Content = JsonContent.Create(envelope) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InboundApplyResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static SyncEnvelope Envelope(Guid organizationId, Guid branchId, Guid saleId, string payload) => new(
        OperationId: Guid.NewGuid(), ContractVersion: 1, OrganizationId: organizationId, BranchId: branchId,
        AggregateId: saleId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "sale", Payload: payload);

    private static string? ReadInboxPayload(Guid operationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT payload::text FROM sync_inbox WHERE operation_id = $1", owner);
        cmd.Parameters.AddWithValue(operationId);
        return cmd.ExecuteScalar() as string;
    }

    private static long CountInbox(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM sync_inbox WHERE organization_id = $1", owner);
        cmd.Parameters.AddWithValue(organizationId);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task SaleWithACashTender_IsStoredWithItsTender_AndRedeliveryStoresNothingNew()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var payload = SyncPayloadCodec.Serialize(new SalePayloadV1(
            saleId, 855m, "Scanned", DateTimeOffset.UtcNow, [new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 855m, 855m)],
            Tender: new SaleTender("cash", 1000m, 145m)));
        var envelope = Envelope(organizationId, branchId, saleId, payload);

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, envelope)).Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, (await PushAsync(deviceToken, envelope)).Outcome);

        var stored = JsonDocument.Parse(ReadInboxPayload(envelope.OperationId)!).RootElement.GetProperty("Tender");
        Assert.Equal("cash", stored.GetProperty("Method").GetString());
        Assert.Equal(1000m, stored.GetProperty("AmountReceived").GetDecimal());
        Assert.Equal(145m, stored.GetProperty("ChangeGiven").GetDecimal());
        Assert.Equal(1, CountInbox(organizationId));
    }

    [Fact]
    public async Task ManualSaleWithACardTender_IsStoredWithTheMethod()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var payload = SyncPayloadCodec.Serialize(new SalePayloadV1(
            saleId, 500m, "Manual", DateTimeOffset.UtcNow, [], CustomerId: Guid.NewGuid(), Tender: SaleTenderRules.Card()));
        var envelope = Envelope(organizationId, branchId, saleId, payload);

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, envelope)).Outcome);

        var root = JsonDocument.Parse(ReadInboxPayload(envelope.OperationId)!).RootElement;
        Assert.Equal("card", root.GetProperty("Tender").GetProperty("Method").GetString());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("CustomerId").ValueKind);
    }

    [Fact]
    public async Task PayloadFromBeforeTenders_StillIngests()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var legacy = $$"""{"saleId":"{{saleId}}","totalAmount":10,"saleKind":"Manual","occurredAtUtc":"2026-01-01T00:00:00+00:00","lines":[]}""";
        var envelope = Envelope(organizationId, branchId, saleId, legacy);

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, envelope)).Outcome);
        Assert.NotNull(ReadInboxPayload(envelope.OperationId));
    }
}
