using System.Collections.Concurrent;
using System.Net;

namespace Commerce.Cloud.Api.Authentication;

/// <summary>
/// In-memory, fixed-window anti-abuse throttle for the anonymous
/// reset-request endpoint (commerce-password-recovery design.md "Anti-abuse
/// mechanism" / "Throttle thresholds"). Two independent key spaces —
/// normalized email and source IP — each capped at 10,000 entries with
/// oldest-window eviction so IP-cycling or address-cycling cannot grow this
/// unboundedly. Accepted single-Railway-replica assumption, the same one
/// <see cref="BootstrapTokenRegistry"/> already documents. Registered as a
/// singleton in Program.cs.
/// </summary>
public sealed class ResetRequestThrottle
{
    private const int MaxEntriesPerKeySpace = 10_000;

    private static readonly TimeSpan EmailWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EmailHourlyWindow = TimeSpan.FromHours(1);
    private const int EmailHourlyLimit = 3;

    private static readonly TimeSpan IpHourlyWindow = TimeSpan.FromHours(1);
    private const int IpHourlyLimit = 10;

    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, EmailWindowState> _byEmail = new();
    private readonly ConcurrentDictionary<string, IpWindowState> _byIp = new();

    public ResetRequestThrottle(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns false when either the per-email or the per-IP threshold is
    /// exceeded — the caller must still respond with the same empty-body 202
    /// (design.md "Throttled response"), simply skipping token issuance and
    /// email dispatch.
    /// </summary>
    public bool TryAcquire(string emailNormalized, IPAddress? remoteIp)
    {
        var now = _clock();

        if (!TryAcquireEmail(emailNormalized, now))
        {
            return false;
        }

        var ipKey = remoteIp?.ToString() ?? "unknown";
        if (!TryAcquireIp(ipKey, now))
        {
            return false;
        }

        return true;
    }

    private bool TryAcquireEmail(string emailNormalized, DateTimeOffset now)
    {
        EvictOldestIfAtCap(_byEmail);

        while (true)
        {
            if (_byEmail.TryGetValue(emailNormalized, out var existing))
            {
                // Prune requests older than the 1-hour cap window; anything
                // still inside it counts toward the hourly limit.
                var recent = existing.RequestTimestamps.Where(t => now - t < EmailHourlyWindow).ToList();

                var withinShortWindow = recent.Count > 0 && now - recent[^1] < EmailWindow;
                if (withinShortWindow)
                {
                    return false;
                }

                if (recent.Count >= EmailHourlyLimit)
                {
                    return false;
                }

                recent.Add(now);
                var updated = existing with { RequestTimestamps = recent };
                if (_byEmail.TryUpdate(emailNormalized, updated, existing))
                {
                    return true;
                }

                continue; // Concurrent update — retry.
            }

            var created = new EmailWindowState([now]);
            if (_byEmail.TryAdd(emailNormalized, created))
            {
                return true;
            }
        }
    }

    private bool TryAcquireIp(string ipKey, DateTimeOffset now)
    {
        EvictOldestIfAtCap(_byIp);

        while (true)
        {
            if (_byIp.TryGetValue(ipKey, out var existing))
            {
                var recent = existing.RequestTimestamps.Where(t => now - t < IpHourlyWindow).ToList();
                if (recent.Count >= IpHourlyLimit)
                {
                    return false;
                }

                recent.Add(now);
                var updated = existing with { RequestTimestamps = recent };
                if (_byIp.TryUpdate(ipKey, updated, existing))
                {
                    return true;
                }

                continue; // Concurrent update — retry.
            }

            var created = new IpWindowState([now]);
            if (_byIp.TryAdd(ipKey, created))
            {
                return true;
            }
        }
    }

    private static void EvictOldestIfAtCap<TState>(ConcurrentDictionary<string, TState> dictionary)
        where TState : IHasOldestTimestamp
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

    private interface IHasOldestTimestamp
    {
        DateTimeOffset OldestTimestamp { get; }
    }

    private sealed record EmailWindowState(List<DateTimeOffset> RequestTimestamps) : IHasOldestTimestamp
    {
        public DateTimeOffset OldestTimestamp => RequestTimestamps.Count == 0 ? DateTimeOffset.MaxValue : RequestTimestamps[0];
    }

    private sealed record IpWindowState(List<DateTimeOffset> RequestTimestamps) : IHasOldestTimestamp
    {
        public DateTimeOffset OldestTimestamp => RequestTimestamps.Count == 0 ? DateTimeOffset.MaxValue : RequestTimestamps[0];
    }
}
