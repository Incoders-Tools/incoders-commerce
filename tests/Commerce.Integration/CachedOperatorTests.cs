using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pos-user-login task 1.3 (design.md "Staleness TTL and
/// reconciliation trigger"): 14-day TTL since `LastVerifiedUtc`, exercising
/// the exact boundary.
/// </summary>
public sealed class CachedOperatorTests
{
    private static CachedOperator MakeOperator(DateTimeOffset lastVerifiedUtc) => new(
        Guid.NewGuid(), "operator@example.com", Guid.NewGuid(), [1, 2, 3], [4, 5, 6], lastVerifiedUtc);

    [Fact]
    public void IsStale_At13Days_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var op = MakeOperator(now - TimeSpan.FromDays(13));

        Assert.False(op.IsStale(now));
    }

    [Fact]
    public void IsStale_At15Days_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var op = MakeOperator(now - TimeSpan.FromDays(15));

        Assert.True(op.IsStale(now));
    }

    [Fact]
    public void IsStale_AtExactly14DayBoundary_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var op = MakeOperator(now - TimeSpan.FromDays(14));

        Assert.False(op.IsStale(now));
    }

    [Fact]
    public void IsStale_JustPast14DayBoundary_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var op = MakeOperator(now - TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));

        Assert.True(op.IsStale(now));
    }
}
