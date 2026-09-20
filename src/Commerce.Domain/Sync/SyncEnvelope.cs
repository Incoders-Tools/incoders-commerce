using System.Text.Json;

namespace Commerce.Domain.Sync;

/// <summary>
/// Envelope shape for every branch/cloud synchronization operation (ADR-003).
/// Carries contract/tenant/branch/aggregate versions, actor, and correlation
/// so idempotency and conflict detection never rely on wall-clock ordering.
///
/// Phase F (commerce-sync-ownership design.md "SyncEnvelope.Payload"):
/// <see cref="Payload"/> is kept and made real, with a constructor guard — a
/// return to the decorative constant <c>"{}"</c> becomes a construction-time
/// failure, not a convention. <see cref="ContractVersion"/> stays frozen at
/// <c>1</c>: a breaking payload-kind change ships as a new
/// <see cref="PayloadKind"/> (for example <c>"sale.v2"</c>), never a version
/// bump — there is no negotiation code anywhere in this system.
/// </summary>
public sealed record SyncEnvelope(
    Guid OperationId,
    int ContractVersion,
    Guid OrganizationId,
    Guid BranchId,
    Guid AggregateId,
    long AggregateVersion,
    Guid ActorId,
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string PayloadKind,
    string Payload)
{
    /// <summary>
    /// Rejects the decorative placeholder <c>"{}"</c> and any value that is
    /// not a valid JSON object. The initializer expression runs as part of
    /// the primary constructor, so validation happens at construction time —
    /// a return to the decorative constant becomes a construction-time
    /// failure, not a convention.
    /// </summary>
    public string Payload { get; init; } = ValidatePayload(Payload);

    private static string ValidatePayload(string payload)
    {
        if (payload == "{}")
        {
            throw new ArgumentException(
                "SyncEnvelope.Payload must carry real payload data — the decorative placeholder \"{}\" is not a valid envelope payload.",
                nameof(payload));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("SyncEnvelope.Payload must be valid JSON.", nameof(payload), ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("SyncEnvelope.Payload must be a JSON object.", nameof(payload));
            }
        }

        return payload;
    }
}
