using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-sync-ownership design.md Unit 1 (payload contract):
/// <see cref="SyncEnvelope.Payload"/> rejects the decorative <c>"{}"</c>
/// placeholder and non-JSON-object values; <see cref="SalePayloadV1"/>/
/// <see cref="OrderPayloadV1"/> round-trip through <see cref="SyncPayloadCodec"/>;
/// an unknown JSON member deserializes without throwing (Requirement:
/// Payload-Kind Versioning, additive-evolution rule).
/// </summary>
public sealed class SyncPayloadTests
{
    private static SyncEnvelope NewEnvelope(string payload) => new(
        OperationId: Guid.NewGuid(), ContractVersion: 1, OrganizationId: Guid.NewGuid(), BranchId: Guid.NewGuid(),
        AggregateId: Guid.NewGuid(), AggregateVersion: 1, ActorId: Guid.NewGuid(), CorrelationId: Guid.NewGuid(),
        OccurredAtUtc: DateTimeOffset.UtcNow, PayloadKind: "sale", Payload: payload);

    [Fact]
    public void SyncEnvelope_Constructor_Rejects_DecorativePlaceholder()
    {
        Assert.Throws<ArgumentException>(() => NewEnvelope("{}"));
    }

    [Fact]
    public void SyncEnvelope_Constructor_Rejects_NonJsonObjectPayload()
    {
        Assert.Throws<ArgumentException>(() => NewEnvelope("not json"));
        Assert.Throws<ArgumentException>(() => NewEnvelope("[]"));
        Assert.Throws<ArgumentException>(() => NewEnvelope("\"a string\""));
    }

    [Fact]
    public void SalePayloadV1_RoundTrips_ThroughCodec()
    {
        var saleId = Guid.NewGuid();
        var lines = new[]
        {
            new SaleLine(saleId, 1, Guid.NewGuid(), "code-1", "Product", "Presentation", 2m, 10m, 20m)
        };
        var payload = new SalePayloadV1(saleId, 20m, "Scanned", DateTimeOffset.UtcNow, lines);

        var json = SyncPayloadCodec.Serialize(payload);
        var roundTripped = SyncPayloadCodec.Deserialize<SalePayloadV1>(json);

        Assert.Equivalent(payload, roundTripped, strict: true);
    }

    [Fact]
    public void OrderPayloadV1_RoundTrips_ThroughCodec()
    {
        var orderId = Guid.NewGuid();
        var lines = new[]
        {
            new OrderLinePayloadV1(Guid.NewGuid(), "Product", Guid.NewGuid(), "Presentation", 3m, 5m, 15m)
        };
        var payload = new OrderPayloadV1(orderId, Guid.NewGuid(), "RegisteredCustomer", lines);

        var json = SyncPayloadCodec.Serialize(payload);
        var roundTripped = SyncPayloadCodec.Deserialize<OrderPayloadV1>(json);

        Assert.Equivalent(payload, roundTripped, strict: true);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownMember_AdditiveEvolutionRule()
    {
        var saleId = Guid.NewGuid();
        var json = $$"""
            {"SaleId":"{{saleId}}","TotalAmount":10,"SaleKind":"Manual","OccurredAtUtc":"2026-01-01T00:00:00+00:00","Lines":[],"FutureField":"ignored"}
            """;

        var payload = SyncPayloadCodec.Deserialize<SalePayloadV1>(json);

        Assert.Equal(saleId, payload.SaleId);
    }
}
