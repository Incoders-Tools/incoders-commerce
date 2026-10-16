using System.Text.Json;

namespace Commerce.Domain.Sync;

/// <summary>
/// The one <see cref="System.Text.Json"/> serialize/deserialize pair for
/// every <see cref="SyncEnvelope.Payload"/> body (commerce-sync-ownership
/// design.md File Changes). <see cref="JsonSerializerOptions"/> ignores
/// unknown members on read — the additive-evolution rule (Requirement:
/// Payload-Kind Versioning) enforced by options, not by convention: a field
/// added to a later payload version never throws for a reader written
/// against an earlier version of the same kind.
/// </summary>
public static class SyncPayloadCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<TPayload>(TPayload payload) =>
        JsonSerializer.Serialize(payload, Options);

    public static TPayload Deserialize<TPayload>(string json) =>
        JsonSerializer.Deserialize<TPayload>(json, Options)
        ?? throw new InvalidOperationException($"Payload deserialized to null for type {typeof(TPayload).Name}.");
}
