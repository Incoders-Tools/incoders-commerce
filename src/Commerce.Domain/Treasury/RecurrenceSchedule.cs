namespace Commerce.Domain.Treasury;

/// <summary>How often a recurring treasury movement repeats.</summary>
public static class RecurrenceFrequency
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static bool IsValid(string? value) => value is Weekly or Monthly or Yearly;
}

/// <summary>When a recurrence stops: never, after a date, or after a number of occurrences.</summary>
public static class RecurrenceEnd
{
    public const string Never = "Never";
    public const string OnDate = "OnDate";
    public const string AfterCount = "AfterCount";

    public static bool IsValid(string? value) => value is Never or OnDate or AfterCount;
}

/// <summary>
/// The dates of a recurring movement: every <see cref="Interval"/> weeks, months or years from <see cref="Start"/>
/// (the weekday, the day of the month or the date of the year come from the start date; a day the month does not have
/// becomes its last day, so "the 31st" is the 30th in April and the 28th or 29th in February), until its end. Pure: the
/// generation of the movements and the screens use the same dates.
/// </summary>
public sealed record RecurrenceSchedule(
    DateOnly Start, string Frequency, int Interval, string EndMode, DateOnly? EndDate, int? MaxOccurrences)
{
    public const int MaxInterval = 24;

    /// <summary>The n-th date (0 = the start date).</summary>
    public DateOnly OccurrenceAt(int index) => Frequency switch
    {
        RecurrenceFrequency.Weekly => Start.AddDays(7 * Interval * index),
        RecurrenceFrequency.Yearly => Clamp(Start.Year + Interval * index, Start.Month, Start.Day),
        _ => MonthOccurrence(index),
    };

    private DateOnly MonthOccurrence(int index)
    {
        var months = Start.Month - 1 + Interval * index;
        return Clamp(Start.Year + months / 12, months % 12 + 1, Start.Day);
    }

    private static DateOnly Clamp(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    /// <summary>Whether the n-th date is still within the recurrence (not past its end date nor its count).</summary>
    public bool Includes(int index, DateOnly date) => EndMode switch
    {
        RecurrenceEnd.OnDate => EndDate is { } end && date <= end,
        RecurrenceEnd.AfterCount => MaxOccurrences is { } max && index < max,
        _ => true,
    };

    /// <summary>Every date of the recurrence up to <paramref name="until"/> (inclusive), in order.</summary>
    public IEnumerable<DateOnly> OccurrencesUntil(DateOnly until)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var date = OccurrenceAt(index);
            if (date > until || !Includes(index, date))
            {
                yield break;
            }

            yield return date;
        }
    }

    /// <summary>The first date after <paramref name="after"/>, or null when the recurrence ended before it.</summary>
    public DateOnly? NextAfter(DateOnly after)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var date = OccurrenceAt(index);
            if (!Includes(index, date))
            {
                return null;
            }

            if (date > after)
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>Whether the schedule is well formed (the end fits the mode, the interval is 1 to 24).</summary>
    public bool IsValid() =>
        RecurrenceFrequency.IsValid(Frequency) && Interval is >= 1 and <= MaxInterval && RecurrenceEnd.IsValid(EndMode)
        && EndMode switch
        {
            RecurrenceEnd.OnDate => EndDate is { } end && end >= Start && MaxOccurrences is null,
            RecurrenceEnd.AfterCount => MaxOccurrences is >= 1 and <= 1000 && EndDate is null,
            _ => EndDate is null && MaxOccurrences is null,
        };
}
