using System.Collections.Concurrent;
using Commerce.Cloud.Api.Persistence;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// 60-second-TTL cache of each organization's account-standing INPUTS (organization-account-standing T4), read by
/// <see cref="OrganizationSuspensionMiddleware"/> on every staff request and by <c>/account/me</c>. It caches the inputs,
/// not the derived status, so a business-day change still moves the standing on the next read. Same pattern and
/// staleness bound as <see cref="SessionVersionCache"/>: every system-administrator write calls
/// <see cref="Invalidate"/> in the same request, so on a single replica a suspension or reactivation applies on the
/// very next request, and on a future second replica within 60 seconds, never "never".
/// </summary>
public sealed class OrganizationStandingCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly PostgresOrganizationStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public OrganizationStandingCache(PostgresOrganizationStore store, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Called by every account-standing write right after it commits.</summary>
    public void Invalidate(Guid organizationId) => _entries.TryRemove(organizationId, out _);

    /// <summary>The cached inputs while within the TTL; otherwise reloaded. <c>null</c> when the organization does not exist.</summary>
    public async Task<OrganizationAccountStandingInputs?> GetAsync(Guid organizationId, CancellationToken ct)
    {
        if (_entries.TryGetValue(organizationId, out var entry) && entry.ExpiresAtUtc > _clock())
        {
            return entry.Inputs;
        }

        var inputs = await _store.GetAccountStandingInputsAsync(organizationId, ct);
        _entries[organizationId] = new Entry(inputs, _clock().Add(Ttl));
        return inputs;
    }

    private readonly record struct Entry(OrganizationAccountStandingInputs? Inputs, DateTimeOffset ExpiresAtUtc);
}
