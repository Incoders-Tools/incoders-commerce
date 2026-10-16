using System.Collections.Concurrent;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// 60-second-TTL session-version cache (commerce-password-recovery design.md
/// "Session invalidation"). `CookieAuthenticationEvents.OnValidatePrincipal`
/// reads through this on every authenticated request instead of hitting
/// Postgres directly; every password-change path calls <see cref="Set"/> in
/// the SAME request that changes the password, so invalidation is immediate
/// on a single-replica deployment (the accepted assumption `railway.json`
/// already documents) and degrades to a documented <= 60s staleness bound on
/// a future second replica — never to "never".
/// </summary>
public sealed class SessionVersionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly PostgresPasswordRecoveryStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public SessionVersionCache(PostgresPasswordRecoveryStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Write-through: called by every password-change path immediately after
    /// committing the new `session_version`, so the SAME process sees the
    /// new value on its very next read with zero DB round-trip.
    /// </summary>
    public void Set(Guid userId, int version) => _entries[userId] = new Entry(version, _clock().Add(Ttl));

    /// <summary>
    /// Returns the cached version if still within the 60s TTL; otherwise
    /// reloads from Postgres (org-scoped) and refreshes the cache entry.
    /// Returns null when the user does not exist (cross-org / revoked-away).
    /// </summary>
    public async Task<int?> GetAsync(CloudTenantScope scope, Guid userId, CancellationToken ct)
    {
        if (_entries.TryGetValue(userId, out var entry) && entry.ExpiresAtUtc > _clock())
        {
            return entry.Version;
        }

        var version = await _store.GetSessionVersionAsync(scope, userId, ct);
        if (version is not null)
        {
            Set(userId, version.Value);
        }

        return version;
    }

    private readonly record struct Entry(int Version, DateTimeOffset ExpiresAtUtc);
}
