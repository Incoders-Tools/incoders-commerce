using System.Collections.Concurrent;
using System.Net;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// In-memory, fixed-window anti-abuse throttle for the anonymous guest
/// verification-request endpoint (commerce-guest-ordering design.md "Rate
/// limiting"): a SECOND, identity-keyed layer on top of the coarser IP-only
/// ASP.NET rate limiter (Unit 5) — it stops one contact address being mailed
/// repeatedly from rotating IPs, which an IP-only limiter cannot see. The
/// exact <see cref="ResetRequestThrottle"/> shape: two independent key
/// spaces, each capped at 10,000 entries with oldest-window eviction so
/// address- or IP-cycling cannot grow this unboundedly. Accepted
/// single-Railway-replica assumption, the same one
/// <see cref="ResetRequestThrottle"/> and <see cref="BootstrapTokenRegistry"/>
/// already document. Registered as a singleton in Program.cs.
/// </summary>
public sealed class GuestVerificationThrottle
{
    private const int MaxEntriesPerKeySpace = 10_000;

    private static readonly TimeSpan AddressWindow = TimeSpan.FromMinutes(15);
    private const int AddressLimit = 5;

    private static readonly TimeSpan IpWindow = TimeSpan.FromHours(1);
    private const int IpLimit = 20;

    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, WindowState> _byAddress = new();
    private readonly ConcurrentDictionary<string, WindowState> _byIp = new();

    public GuestVerificationThrottle(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns false when either the per-address or the per-IP threshold is
    /// exceeded — the caller must still respond with the same 202 (the
    /// reset-password precedent), simply skipping code issuance and email
    /// dispatch.
    /// </summary>
    public bool TryAcquire(string contactAddressNormalized, IPAddress? remoteIp)
    {
        var now = _clock();

        if (!TryAcquireKey(_byAddress, contactAddressNormalized, AddressWindow, AddressLimit, now))
        {
            return false;
        }

        var ipKey = remoteIp?.ToString() ?? "unknown";
        if (!TryAcquireKey(_byIp, ipKey, IpWindow, IpLimit, now))
        {
            return false;
        }

        return true;
    }

    private static bool TryAcquireKey(
        ConcurrentDictionary<string, WindowState> dictionary, string key, TimeSpan window, int limit, DateTimeOffset now)
    {
        EvictOldestIfAtCap(dictionary);

        while (true)
        {
            if (dictionary.TryGetValue(key, out var existing))
            {
                var recent = existing.Timestamps.Where(t => now - t < window).ToList();
                if (recent.Count >= limit)
                {
                    return false;
                }

                recent.Add(now);
                var updated = existing with { Timestamps = recent };
                if (dictionary.TryUpdate(key, updated, existing))
                {
                    return true;
                }

                continue; // Concurrent update — retry.
            }

            var created = new WindowState([now]);
            if (dictionary.TryAdd(key, created))
            {
                return true;
            }
        }
    }

    private static void EvictOldestIfAtCap(ConcurrentDictionary<string, WindowState> dictionary)
    {
        if (dictionary.Count < MaxEntriesPerKeySpace)
        {
            return;
        }

        string? oldestKey = null;
        var oldestTimestamp = DateTimeOffset.MaxValue;
        foreach (var pair in dictionary)
        {
            if (pair.Value.OldestTimestamp < oldestTimestamp)
            {
                oldestTimestamp = pair.Value.OldestTimestamp;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is not null)
        {
            dictionary.TryRemove(oldestKey, out _);
        }
    }

    private sealed record WindowState(List<DateTimeOffset> Timestamps)
    {
        public DateTimeOffset OldestTimestamp => Timestamps.Count == 0 ? DateTimeOffset.MaxValue : Timestamps[0];
    }
}
