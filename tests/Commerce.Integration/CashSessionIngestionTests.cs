using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Commerce.Integration;

/// <summary>
/// pos-cash-session "Session Synchronized and Audited", cloud side: opened and
/// closed payloads are stored in the sync inbox as received and audited once
/// each (the close with its difference); redelivery repeats nothing; a sale
/// payload with or without a session id, and an unreadable session payload,
/// still ingest.
/// </summary>
[Collection("Postgres")]
public sealed class CashSessionIngestionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Password = "correct-password";

    private readonly bool _postgresAvailable = PostgresTestFixture.TryPing(PostgresTestFixture.DirectConnectionString);
    private readonly WebApplicationFactory<Program> _factory;

    public CashSessionIngestionTests(WebApplicationFactory<Program> factory)
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
            new BootstrapRequest(organizationId, token, "Org " + organizationId, "HQ", $"cash-{organizationId:N}@example.com", Password));
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

    private static SyncEnvelope Envelope(
        Guid organizationId, Guid branchId, Guid aggregateId, string kind, string payload, Guid? actorId = null) => new(
        OperationId: Guid.NewGuid(), ContractVersion: 1, OrganizationId: organizationId, BranchId: branchId,
        AggregateId: aggregateId, AggregateVersion: 1, ActorId: actorId ?? Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: kind, Payload: payload);

    private static SyncEnvelope Opened(Guid organizationId, Guid branchId, Guid sessionId, Guid operatorId) => Envelope(
        organizationId, branchId, sessionId, CashSessionPayloadKinds.Opened,
        SyncPayloadCodec.Serialize(new CashSessionOpenedPayloadV1(sessionId, operatorId, 5000m, DateTimeOffset.UtcNow)), operatorId);

    private static SyncEnvelope Closed(Guid organizationId, Guid branchId, Guid sessionId, Guid operatorId) => Envelope(
        organizationId, branchId, sessionId, CashSessionPayloadKinds.Closed,
        SyncPayloadCodec.Serialize(new CashSessionClosedPayloadV1(
            sessionId, operatorId, 5000m, DateTimeOffset.UtcNow, 4, 955m, 300m, 200m, 0m, 5955m, 5900m, -55m)), operatorId);

    private static List<(Guid ActorId, Guid EntityId, string Action, string NewValue)> ReadSessionAudit(Guid organizationId)
    {
        using var owner = new NpgsqlConnection(PostgresTestFixture.OwnerConnectionString);
        owner.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT actor_id, entity_id, action, new_value::text FROM audit_log WHERE organization_id = $1 AND entity_type = 'cash-session' ORDER BY id", owner);
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
    public void AuditBuilder_ReturnsNothingForAnUnrelatedOrUnreadablePayload()
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();

        Assert.Null(CashSessionAudit.TryBuild(Envelope(org, branch, Guid.NewGuid(), "sale", """{"saleId":"x"}""")));
        Assert.Null(CashSessionAudit.TryBuild(Envelope(org, branch, Guid.NewGuid(), CashSessionPayloadKinds.Closed, """{"sessionId":"not-a-guid"}""")));
    }

    [Fact]
    public void AuditBuilder_RecordsTheDifferenceOnAClose()
    {
        var org = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();

        var entry = CashSessionAudit.TryBuild(Closed(org, Guid.NewGuid(), sessionId, operatorId))!;

        Assert.Equal("cash-session.closed", entry.Action);
        Assert.Equal("cash-session", entry.EntityType);
        Assert.Equal(sessionId, entry.EntityId);
        Assert.Equal(operatorId, entry.ActorId);
        Assert.Equal(org, entry.OrganizationId);
        var detail = JsonDocument.Parse(entry.NewValueJson!).RootElement;
        Assert.Equal(-55m, detail.GetProperty("difference").GetDecimal());
        Assert.Equal(5955m, detail.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(5900m, detail.GetProperty("countedCash").GetDecimal());
    }

    [Fact]
    public async Task OpenedSession_IsStoredAsReceived_AndAuditedOnce()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var sessionId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var envelope = Opened(organizationId, branchId, sessionId, operatorId);

        var result = await PushAsync(deviceToken, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        var stored = JsonDocument.Parse(ReadInboxPayload(envelope.OperationId)!).RootElement;
        Assert.Equal(5000m, stored.GetProperty("OpeningFloat").GetDecimal());
        var audit = Assert.Single(ReadSessionAudit(organizationId));
        Assert.Equal("cash-session.opened", audit.Action);
        Assert.Equal(sessionId, audit.EntityId);
        Assert.Equal(operatorId, audit.ActorId);
        Assert.Equal(5000m, JsonDocument.Parse(audit.NewValue).RootElement.GetProperty("openingFloat").GetDecimal());
    }

    [Fact]
    public async Task ClosedSession_IsAuditedWithItsDifference_AndRedeliveryWritesNothingNew()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var sessionId = Guid.NewGuid();
        var envelope = Closed(organizationId, branchId, sessionId, Guid.NewGuid());

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, envelope)).Outcome);
        Assert.Equal(InboundApplyOutcome.DuplicateIgnored, (await PushAsync(deviceToken, envelope)).Outcome);

        var audit = Assert.Single(ReadSessionAudit(organizationId));
        Assert.Equal("cash-session.closed", audit.Action);
        Assert.Equal(-55m, JsonDocument.Parse(audit.NewValue).RootElement.GetProperty("difference").GetDecimal());
    }

    [Fact]
    public async Task UnreadableSessionPayload_StillIngests_WithNoAuditRow()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var envelope = Envelope(organizationId, branchId, Guid.NewGuid(), CashSessionPayloadKinds.Closed, """{"note":"from a newer terminal"}""");

        var result = await PushAsync(deviceToken, envelope);

        Assert.Equal(InboundApplyOutcome.Applied, result.Outcome);
        Assert.NotNull(ReadInboxPayload(envelope.OperationId));
        Assert.Empty(ReadSessionAudit(organizationId));
    }

    [Fact]
    public async Task SalePayload_WithAndWithoutASessionId_StillIngests()
    {
        if (!_postgresAvailable) return;
        var (organizationId, branchId, deviceToken) = await PairedTerminalAsync();
        var saleId = Guid.NewGuid();
        var withSession = Envelope(organizationId, branchId, saleId, "sale", SyncPayloadCodec.Serialize(new SalePayloadV1(
            saleId, 100m, "Manual", DateTimeOffset.UtcNow, [], CashSessionId: Guid.NewGuid())));
        var legacyId = Guid.NewGuid();
        var legacy = Envelope(organizationId, branchId, legacyId, "sale",
            $$"""{"saleId":"{{legacyId}}","totalAmount":10,"saleKind":"Manual","occurredAtUtc":"2026-01-01T00:00:00+00:00","lines":[]}""");

        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, withSession)).Outcome);
        Assert.Equal(InboundApplyOutcome.Applied, (await PushAsync(deviceToken, legacy)).Outcome);

        Assert.True(JsonDocument.Parse(ReadInboxPayload(withSession.OperationId)!).RootElement.TryGetProperty("CashSessionId", out _));
        Assert.Empty(ReadSessionAudit(organizationId));
    }
}
