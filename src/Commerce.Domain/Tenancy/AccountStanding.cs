namespace Commerce.Domain.Tenancy;

/// <summary>Where an organization stands on its payment to the platform (odd/tasks/organization-account-standing.md).</summary>
public enum AccountStandingStatus
{
    /// <summary>No due date, or not past it yet.</summary>
    Active,

    /// <summary>Past the due date and inside the grace days: everything works, administrators see the countdown.</summary>
    Overdue,

    /// <summary>Grace exhausted or suspended by the system administrator: web access is blocked, the POS only warns.</summary>
    Suspended
}

/// <summary>
/// A derived standing. <see cref="SuspendsOn"/> is the first suspended business day (set while overdue, and once
/// suspended by the dates; null for a manual suspension). <see cref="DaysLeft"/> is set only while overdue, from the
/// grace days down to 1.
/// </summary>
public sealed record AccountStanding(AccountStandingStatus Status, DateOnly? SuspendsOn, int? DaysLeft);

/// <summary>
/// The standing is DERIVED from the stored inputs on every read, never stored, so it moves on its own when the business
/// day changes and needs no scheduled job. The cloud and the POS run this same rule; the POS runs it offline on the last
/// synced inputs so its countdown stays right without a connection.
/// </summary>
public static class AccountStandingRules
{
    public const int DefaultGraceDays = 30;
    public const int MaxGraceDays = 90;

    public static bool IsValidGraceDays(int graceDays) => graceDays is >= 0 and <= MaxGraceDays;

    /// <param name="dueOn">Billing due date; null means billing is not tracked and the organization stays active.</param>
    /// <param name="graceDays">Days after <paramref name="dueOn"/> that still work, 0 to <see cref="MaxGraceDays"/>.</param>
    /// <param name="manuallySuspended">A system administrator suspended the organization; wins over any date.</param>
    /// <param name="today">The business day (<c>IBusinessClock</c>), never the machine's UTC date.</param>
    public static AccountStanding Evaluate(DateOnly? dueOn, int graceDays, bool manuallySuspended, DateOnly today)
    {
        // Before the validation: a manual suspension does not depend on the grace days, and the POS evaluates inputs
        // that arrive over the wire, where a bad value must never turn a suspension into an exception.
        if (manuallySuspended)
        {
            return new AccountStanding(AccountStandingStatus.Suspended, null, null);
        }

        if (!IsValidGraceDays(graceDays))
        {
            throw new ArgumentOutOfRangeException(nameof(graceDays), graceDays, $"Grace days must be between 0 and {MaxGraceDays}.");
        }

        if (dueOn is not { } due || today <= due)
        {
            return new AccountStanding(AccountStandingStatus.Active, null, null);
        }

        var suspendsOn = due.AddDays(graceDays + 1);
        return today < suspendsOn
            ? new AccountStanding(AccountStandingStatus.Overdue, suspendsOn, suspendsOn.DayNumber - today.DayNumber)
            : new AccountStanding(AccountStandingStatus.Suspended, suspendsOn, null);
    }
}
