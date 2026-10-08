namespace Commerce.Application.Time;

/// <summary>
/// The BUSINESS day: the calendar date in the business time zone (default America/Argentina/Buenos_Aires). Every effective
/// date of pricing and stock (price entries, rate sets, the replica snapshot, order pricing, receptions) is read from here,
/// never from the UTC date: between 21:00 and 24:00 in Argentina UTC is already "tomorrow". Timestamps stay UTC.
/// </summary>
public interface IBusinessClock
{
    /// <summary>Today's date in the business time zone.</summary>
    DateOnly Today { get; }
}

/// <summary>Resolves the business time zone from an IANA id, with the Windows id as fallback.</summary>
public static class BusinessTimeZone
{
    public const string DefaultId = "America/Argentina/Buenos_Aires";

    private const string WindowsFallbackId = "Argentina Standard Time";

    /// <summary>
    /// The zone for <paramref name="id"/> (null or blank = <see cref="DefaultId"/>). An unknown id throws instead of
    /// silently falling back to UTC, which would reintroduce the early-effective-date defect.
    /// </summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(wanted, out var zone))
        {
            return zone;
        }

        if (wanted == DefaultId && TimeZoneInfo.TryFindSystemTimeZoneById(WindowsFallbackId, out var windows))
        {
            return windows;
        }

        throw new InvalidOperationException($"Business time zone '{wanted}' was not found on this machine.");
    }
}

/// <summary><see cref="IBusinessClock"/> over a <see cref="TimeProvider"/> (fixed in tests) and a time zone.</summary>
public sealed class BusinessClock : IBusinessClock
{
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public BusinessClock(TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _time = time ?? TimeProvider.System;
        _zone = zone ?? BusinessTimeZone.Resolve(null);
    }

    /// <summary>The system clock in the default business zone (composition roots without configuration).</summary>
    public static IBusinessClock System { get; } = new BusinessClock();

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime);
}
