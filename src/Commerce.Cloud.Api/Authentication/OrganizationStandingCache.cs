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
/// <para>
/// A read that loads while a write commits could otherwise cache the pre-write value AFTER the write invalidated it.
/// Each invalidation bumps a per-organization generation, and a load stores its result only when the generation it
/// started on is still current; both happen under one lock, so no invalidation can slip between check and store.
/// </para>
/// </summary>
public sealed class OrganizationStandingCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly Func<Guid, CancellationToken, Task<OrganizationAccountStandingInputs?>> _load;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly Dictionary<Guid, long> _generations = new();
    private readonly Lock _gate = new();

    public OrganizationStandingCache(PostgresOrganizationStore store, Func<DateTimeOffset>? clock = null)
        : this(store.GetAccountStandingInputsAsync, clock)
    {
    }

    private OrganizationStandingCache(Func<Guid, CancellationToken, Task<OrganizationAccountStandingInputs?>> load, Func<DateTimeOffset>? clock)
    {
        _load = load;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A cache over any loader, for tests that control when a load completes.</summary>
    public static OrganizationStandingCache ForLoader(
        Func<Guid, CancellationToken, Task<OrganizationAccountStandingInputs?>> load, Func<DateTimeOffset>? clock = null) =>
        new(load, clock);

    /// <summary>Called by every account-standing write right after it commits.</summary>
    public void Invalidate(Guid organizationId)
    {
        lock (_gate)
        {
            _generations[organizationId] = Generation(organizationId) + 1;
            _entries.TryRemove(organizationId, out _);
        }
    }

    /// <summary>The cached inputs while within the TTL; otherwise reloaded. <c>null</c> when the organization does not exist.</summary>
    public async Task<OrganizationAccountStandingInputs?> GetAsync(Guid organizationId, CancellationToken ct)
    {
        if (_entries.TryGetValue(organizationId, out var entry) && entry.ExpiresAtUtc > _clock())
        {
            return entry.Inputs;
        }

        long startedOn;
        lock (_gate) startedOn = Generation(organizationId);

        var inputs = await _load(organizationId, ct);

        lock (_gate)
        {
            if (Generation(organizationId) == startedOn)
            {
                _entries[organizationId] = new Entry(inputs, _clock().Add(Ttl));
            }
        }
        return inputs;
    }

    /// <summary>Callers hold <see cref="_gate"/>.</summary>
    private long Generation(Guid organizationId) => _generations.GetValueOrDefault(organizationId);

    private readonly record struct Entry(OrganizationAccountStandingInputs? Inputs, DateTimeOffset ExpiresAtUtc);
}
