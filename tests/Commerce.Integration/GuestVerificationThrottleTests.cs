using System.Net;
using Commerce.Cloud.Api.Authentication;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-guest-ordering task 2.7: <see cref="GuestVerificationThrottle"/>
/// — the `ResetRequestThrottle` shape applied to guest verification requests
/// (design.md "Rate limiting"): two independent key spaces (contact address,
/// source IP), each capped at 10,000 entries with oldest-window eviction, so
/// one address being mailed repeatedly from rotating IPs is stopped even
/// though the coarser ASP.NET rate limiter (Unit 5) is IP-only. Pure logic,
/// no live Postgres needed — mirrors <see cref="ResetRequestThrottleTests"/>'s
/// placement convention.
/// </summary>
public sealed class GuestVerificationThrottleTests
{
    private static readonly IPAddress SampleIp = IPAddress.Parse("203.0.113.20");

    [Fact]
    public void TryAcquire_SecondRequestForSameAddress_WithinWindow_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new GuestVerificationThrottle(() => now);

        Assert.True(throttle.TryAcquire("guest-a@example.com", SampleIp));
        Assert.True(throttle.TryAcquire("guest-a@example.com", IPAddress.Parse("198.51.100.40")));
        Assert.True(throttle.TryAcquire("guest-a@example.com", IPAddress.Parse("198.51.100.41")));
        Assert.True(throttle.TryAcquire("guest-a@example.com", IPAddress.Parse("198.51.100.42")));
        Assert.True(throttle.TryAcquire("guest-a@example.com", IPAddress.Parse("198.51.100.43")));
        // 6th request for the same address within the 15-minute window,
        // even from a distinct IP each time, must be denied — the address
        // key space is independent of and not satisfied by IP rotation.
        Assert.False(throttle.TryAcquire("guest-a@example.com", IPAddress.Parse("198.51.100.44")));
    }

    [Fact]
    public void TryAcquire_AfterAddressWindowRollsOver_IsAllowedAgain()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new GuestVerificationThrottle(() => clockBox[0]);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(throttle.TryAcquire("rollover-guest@example.com", SampleIp));
        }
        Assert.False(throttle.TryAcquire("rollover-guest@example.com", SampleIp));

        // Past the 15-minute per-address window.
        clockBox[0] = now.AddMinutes(15).AddSeconds(1);
        Assert.True(throttle.TryAcquire("rollover-guest@example.com", SampleIp));
    }

    [Fact]
    public void TryAcquire_PerIpHourlyThreshold_DeniesRequestBeyondTheCap()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new GuestVerificationThrottle(() => clockBox[0]);

        for (var i = 0; i < 20; i++)
        {
            clockBox[0] = now.AddMinutes(i);
            Assert.True(throttle.TryAcquire($"distinct-guest{i}@example.com", SampleIp));
        }

        clockBox[0] = now.AddMinutes(20);
        Assert.False(throttle.TryAcquire("guest-overflow@example.com", SampleIp));
    }

    [Fact]
    public void TryAcquire_AddressAndIpKeySpaces_AreIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new GuestVerificationThrottle(() => now);

        Assert.True(throttle.TryAcquire("independent-guest-a@example.com", IPAddress.Parse("198.51.100.50")));
        // Same IP, different address, immediately after: allowed because the
        // per-IP threshold (20/hour) has not been reached.
        Assert.True(throttle.TryAcquire("independent-guest-b@example.com", IPAddress.Parse("198.51.100.50")));
    }

    [Fact]
    public void TryAcquire_AtEntryCap_EvictsTheOldestWindowInsteadOfGrowingUnbounded()
    {
        var now = DateTimeOffset.UtcNow;
        var clockBox = new[] { now };
        var throttle = new GuestVerificationThrottle(() => clockBox[0]);

        for (var i = 0; i < 10_000; i++)
        {
            clockBox[0] = now.AddMilliseconds(i);
            throttle.TryAcquire($"capfill-guest{i}@example.com", IPAddress.Parse("198.51.100.60"));
        }

        clockBox[0] = now.AddMilliseconds(10_000);
        Assert.True(throttle.TryAcquire("capfill-guest-overflow@example.com", IPAddress.Parse("198.51.100.61")));
    }
}
