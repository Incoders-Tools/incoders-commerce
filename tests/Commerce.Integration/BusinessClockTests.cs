using Commerce.Application.Time;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists L3: "today" for pricing and stock is the BUSINESS day (America/Argentina/Buenos_Aires, UTC-3, no
/// daylight saving today), never the UTC date: between 21:00 and 24:00 Argentina time UTC is already tomorrow.
/// </summary>
public sealed class BusinessClockTests
{
    [Fact]
    public void At2230InArgentina_ThatIs0130UtcTheNextDay_TodayIsStillTheArgentinaDay()
    {
        var clock = new BusinessClock(new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 1, 30, 0, TimeSpan.Zero)));

        Assert.Equal(new DateOnly(2026, 10, 2), clock.Today);
    }

    [Fact]
    public void AtMidnightInArgentina_ThatIs0300Utc_TodayRollsOver()
    {
        var clock = new BusinessClock(new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 3, 0, 0, TimeSpan.Zero)));

        Assert.Equal(new DateOnly(2026, 10, 3), clock.Today);
    }

    [Fact]
    public void TheDefaultZoneIsBuenosAires_AndAnotherZoneCanBeConfigured()
    {
        var instant = new FixedTimeProvider(new DateTimeOffset(2026, 10, 3, 1, 30, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromHours(-3), BusinessTimeZone.Resolve(null).GetUtcOffset(instant.GetUtcNow()));
        Assert.Equal(new DateOnly(2026, 10, 3), new BusinessClock(instant, BusinessTimeZone.Resolve("UTC")).Today);
    }

    [Fact]
    public void AnUnknownZoneId_IsRefusedAtStartupInsteadOfSilentlyFallingBackToUtc()
    {
        Assert.Throws<InvalidOperationException>(() => BusinessTimeZone.Resolve("Mars/Olympus_Mons"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
