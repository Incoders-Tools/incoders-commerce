using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// In-memory, per-organization one-time bootstrap token issue/consume
/// (design.md "Bootstrap token storage"). No plaintext at rest, no DDL, no
/// cleanup job — tokens die on container restart, bounding the blast radius
/// to one deploy window. Registered as a singleton in Program.cs.
///
/// Multi-replica caveat (design.md "Open Questions"): a Railway deployment
/// with more than one replica would break this — a token issued on instance
/// A could not be redeemed on instance B. Cloud.Api runs a single replica
/// today; if that changes, this registry must move to a persisted table.
/// Documented, accepted for now.
/// </summary>
public sealed class BootstrapTokenRegistry
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(15);

    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<Guid, Entry> _entriesByOrganization = new();

    public BootstrapTokenRegistry(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Issues a fresh token for the given organization, invalidating any
    /// prior unconsumed token for that organization (re-issue supersedes).
    /// Returns the PLAINTEXT token — the caller is responsible for logging it
    /// (design.md "Bootstrap token delivery": stdout only, never HTTP).
    /// </summary>
    public string Issue(Guid organizationId)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var entry = new Entry(HashToken(token), _clock().Add(Expiry));
        _entriesByOrganization[organizationId] = entry;

        return token;
    }

    /// <summary>
    /// Attempts to consume the token for the given organization: must match
    /// (fixed-time hash compare), not be expired, not already consumed, and
    /// be scoped to exactly this organization. One-time-use: a successful
    /// consume marks the entry consumed so a replay always fails.
    /// </summary>
    public bool TryConsume(Guid organizationId, string token)
    {
        if (!_entriesByOrganization.TryGetValue(organizationId, out var entry))
        {
            return false;
        }

        if (entry.Consumed || _clock() > entry.ExpiresAtUtc)
        {
            return false;
        }

        var candidateHash = HashToken(token);
        if (!CryptographicOperations.FixedTimeEquals(candidateHash, entry.TokenHash))
        {
            return false;
        }

        // One-time-use: replace with a consumed marker so replay always fails,
        // even for the correct token.
        return _entriesByOrganization.TryUpdate(organizationId, entry with { Consumed = true }, entry);
    }

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed record Entry(byte[] TokenHash, DateTimeOffset ExpiresAtUtc, bool Consumed = false);
}
