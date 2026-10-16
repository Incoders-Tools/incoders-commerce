using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// pos-scan-sale spec "Discounts Recorded and Synchronized With Their
/// Authorization", cloud side: a discounted sale is ingested with its payload
/// intact and audited exactly once; an older payload without discounts still
/// ingests and writes no discount audit.
/// </summary>
[Collection("Postgres")]
public sealed class SaleDiscountIngestionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public SaleDiscountIngestionTests(WebApplicationFactory<Program> factory)
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
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", $"ingest-{organizationId:N}@example.com", Password));
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

    private static SyncEnvelope Envelope(Guid organizationId, Guid branchId, Guid saleId, string payload, Guid? operationId = null) => new(
        OperationId: operationId ?? Guid.NewGuid(), ContractVersion: 1, OrganizationId: organizationId, BranchId: branchId,
        AggregateId: saleId, AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "sale", Payload: payload);

    private static string DiscountedPayload(Guid saleId, Guid operatorId) => SyncPayloadCodec.Serialize(new SalePayloadV1(
        saleId, 855m, "Scanned", DateTimeOffset.UtcNow,
        [new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 1000m, 1000m, 10m, 100m)],
        CustomerId: null, SaleDiscountPercent: 5m, SaleDiscountAmount: 45m,
        DiscountAuthorization: new DiscountAuthorization(DiscountAuthorization.BranchPin, operatorId, 4)));

    private static List<(Guid ActorId, Guid EntityId, string Action, string NewValue)> ReadSaleAudit(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_id, entity_id, action, new_value::text FROM audit_log WHERE organization_id = $1 AND entity_type = 'sale' ORDER BY id", owner);
        cmd.Parameters.AddWithValue(organizationId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(Guid, Guid, string, string)>();
        while (reader.Read()) rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static string? ReadInboxPayload(Guid operationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand("SELECT payload::text FROM sync_inbox WHERE operation_id = $1", owner);
        cmd.Parameters.AddWithValue(operationId);
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public async Task DiscountedSale_IsStoredWithItsDiscounts_AndAuditedOnceForTheOperator()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var envelope = Envelope(organizationId, branchId, saleId, DiscountedPayload(saleId, operatorId));

        var result = await PushAsync(deviceToken, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        var stored = JsonDocument.Parse(ReadInboxPayload(envelope.OperationId)!).RootElement;
        // The payload is kept as the terminal wrote it (System.Text.Json default names).
        Assert.Equal(45m, stored.GetProperty("SaleDiscountAmount").GetDecimal());
        Assert.Equal("branch-pin", stored.GetProperty("DiscountAuthorization").GetProperty("Method").GetString());
        Assert.Equal(100m, stored.GetProperty("Lines")[0].GetProperty("LineDiscountAmount").GetDecimal());

        var audit = Assert.Single(ReadSaleAudit(organizationId));
        Assert.Equal("sale.discount.authorized", audit.Action);
        Assert.Equal(saleId, audit.EntityId);
        Assert.Equal(operatorId, audit.ActorId);
        var detail = JsonDocument.Parse(audit.NewValue).RootElement;
        Assert.Equal("branch-pin", detail.GetProperty("authorizationMethod").GetString());
        Assert.Equal(operatorId, detail.GetProperty("operatorId").GetGuid());
        Assert.Equal(5m, detail.GetProperty("saleDiscountPercent").GetDecimal());
        Assert.Equal(45m, detail.GetProperty("saleDiscountAmount").GetDecimal());
        Assert.Equal(855m, detail.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(100m, detail.GetProperty("lines")[0].GetProperty("discountAmount").GetDecimal());
    }

    [Fact]
    public async Task RedeliveringTheSameOperation_WritesNoSecondAudit()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var envelope = Envelope(organizationId, branchId, saleId, DiscountedPayload(saleId, Guid.NewGuid()));

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, envelope)).Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, (await PushAsync(deviceToken, envelope)).Outcome);

        Assert.Single(ReadSaleAudit(organizationId));
    }

    [Fact]
    public async Task PayloadFromBeforeDiscounts_StillIngests_AndWritesNoDiscountAudit()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var legacy = $$"""{"saleId":"{{saleId}}","totalAmount":10,"saleKind":"Scanned","occurredAtUtc":"2026-01-01T00:00:00+00:00","lines":[{"saleId":"{{saleId}}","lineNumber":1,"presentationId":"{{Guid.NewGuid()}}","identificationCode":null,"productName":"P","presentationName":"1u","quantity":1,"unitPrice":10,"lineTotal":10}]}""";
        var envelope = Envelope(organizationId, branchId, saleId, legacy);

        var result = await PushAsync(deviceToken, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.NotNull(ReadInboxPayload(envelope.OperationId));
        Assert.Empty(ReadSaleAudit(organizationId));
    }

    [Fact]
    public async Task SaleWithoutDiscounts_WritesNoAudit()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var payload = SyncPayloadCodec.Serialize(new SalePayloadV1(
            saleId, 100m, "Scanned", DateTimeOffset.UtcNow, [new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 100m, 100m)]));

        await PushAsync(deviceToken, Envelope(organizationId, branchId, saleId, payload));

        Assert.Empty(ReadSaleAudit(organizationId));
    }

    [Fact]
    public async Task DiscountsWithoutAnAuthorizationMarker_AreAuditedAsUnauthorized_AndStillIngest()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var payload = SyncPayloadCodec.Serialize(new SalePayloadV1(
            saleId, 90m, "Scanned", DateTimeOffset.UtcNow, [new SaleLine(saleId, 1, Guid.NewGuid(), "c1", "Harina", "1kg", 1m, 100m, 100m)],
            SaleDiscountPercent: 10m, SaleDiscountAmount: 10m));
        var envelope = Envelope(organizationId, branchId, saleId, payload);

        var result = await PushAsync(deviceToken, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.Equal("sale.discount.unauthorized", Assert.Single(ReadSaleAudit(organizationId)).Action);
    }
}
