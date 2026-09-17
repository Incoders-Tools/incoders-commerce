using System.Net;
using Commerce.Cloud.Api.Authentication;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-password-recovery task 3.7: <see cref="ResetRequestThrottle"/>
/// fixed-window per-email/per-IP counters (design.md "Anti-abuse mechanism" /
/// "Throttle thresholds"), with an injected clock. No standalone unit-test
/// project exists in this repo (see `SessionVersionCacheTests` remarks for
/// the same convention), so this lives in `tests/Commerce.Integration` even
/// though it needs no live Postgres.
/// </summary>
public sealed class ResetRequestThrottleTests
{
    private static readonly IPAddress SampleIp = IPAddress.Parse("203.0.113.10");

    [Fact]
    public void TryAcquire_SecondRequestForSameEmail_WithinWindow_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new ResetRequestThrottle(() => now);

        Assert.True(throttle.TryAcquire("first@example.com", SampleIp));
        Assert.False(throttle.TryAcquire("first@example.com", IPAddress.Parse("198.51.100.1")));
    }

    [Fact]
    public void TryAcquire_AfterWindowRollsOver_IsAllowedAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new ResetRequestThrottle(() => clockBox[0]);

        Assert.True(throttle.TryAcquire("rollover@example.com", SampleIp));
        Assert.False(throttle.TryAcquire("rollover@example.com", IPAddress.Parse("198.51.100.2")));

        // Past the 5-minute per-email window.
        clockBox[0] = now.AddMinutes(5).AddSeconds(1);
        Assert.True(throttle.TryAcquire("rollover@example.com", IPAddress.Parse("198.51.100.3")));
    }

    [Fact]
    public void TryAcquire_PerEmailHourlyCap_DeniesTheFourthRequestWithinAnHour()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new ResetRequestThrottle(() => clockBox[0]);

        // 3 requests spaced past the 5-minute per-request window but inside
        // the 1-hour / 3-request cap.
        Assert.True(throttle.TryAcquire("cappedemail@example.com", IPAddress.Parse("198.51.100.10")));
        clockBox[0] = now.AddMinutes(6);
        Assert.True(throttle.TryAcquire("cappedemail@example.com", IPAddress.Parse("198.51.100.11")));
        clockBox[0] = now.AddMinutes(12);
        Assert.True(throttle.TryAcquire("cappedemail@example.com", IPAddress.Parse("198.51.100.12")));
        clockBox[0] = now.AddMinutes(18);
        Assert.False(throttle.TryAcquire("cappedemail@example.com", IPAddress.Parse("198.51.100.13")));
    }

    [Fact]
    public void TryAcquire_PerIpHourlyThreshold_DeniesTheEleventhRequestWithinAnHour()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new ResetRequestThrottle(() => clockBox[0]);

        for (var i = 0; i < 10; i++)
        {
            clockBox[0] = now.AddMinutes(i * 5);
            Assert.True(throttle.TryAcquire($"distinct{i}@example.com", SampleIp));
        }

        clockBox[0] = now.AddMinutes(46);
        Assert.False(throttle.TryAcquire("eleventh@example.com", SampleIp));
    }

    [Fact]
    public void TryAcquire_EmailAndIpKeySpaces_AreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new ResetRequestThrottle(() => now);

        Assert.True(throttle.TryAcquire("independent-a@example.com", IPAddress.Parse("198.51.100.20")));
        // Same IP, different email, immediately after: allowed by the email
        // window (different key) but this call alone must not be denied
        // purely because the IP already recorded one request — the per-IP
        // threshold (10/hour) has not been reached.
        Assert.True(throttle.TryAcquire("independent-b@example.com", IPAddress.Parse("198.51.100.20")));
    }

    [Fact]
    public void TryAcquire_AtEntryCap_EvictsTheOldestWindowInsteadOfGrowingUnbounded()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new ResetRequestThrottle(() => clockBox[0]);

        // Fill the per-email key space to its 10,000-entry cap, then verify a
        // fresh key still succeeds (eviction happened) rather than the
        // throttle silently growing forever or rejecting on capacity.
        for (var i = 0; i < 10_000; i++)
        {
            clockBox[0] = now.AddMilliseconds(i);
            throttle.TryAcquire($"capfill{i}@example.com", IPAddress.Parse("198.51.100.30"));
        }

        clockBox[0] = now.AddMilliseconds(10_000);
        Assert.True(throttle.TryAcquire("capfill-overflow@example.com", IPAddress.Parse("198.51.100.31")));
    }
}
