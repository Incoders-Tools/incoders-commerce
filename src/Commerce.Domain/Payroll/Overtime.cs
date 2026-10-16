using System.Globalization;
using Commerce.Domain.Pricing;

namespace Commerce.Domain.Payroll;

/// <summary>
/// Overtime (employee-lifecycle E2, owner decision 2026-10-11): hours x the employee's overtime hourly rate x a multiplier
/// per record. Sundays are paid double, so a Sunday proposes x2 and any other day x1; either can be changed. The amount is
/// frozen when recorded, so a later rate change never moves it. The payroll takes the pending records into the payslip.
/// </summary>
public static class OvertimeRules
{
    public const decimal MaxHours = 24m;
    public const decimal MinMultiplier = 1m;
    public const decimal MaxMultiplier = 3m;
    public const int MaxNoteLength = 200;

    public static decimal ProposedMultiplier(DateOnly workDate) => workDate.DayOfWeek == DayOfWeek.Sunday ? 2m : 1m;

    public static decimal Amount(decimal hours, decimal hourlyRate, decimal multiplier) => Money.Round2(hours * hourlyRate * multiplier);

    public static bool IsValidHours(decimal hours) => hours is > 0m and <= MaxHours && decimal.Round(hours, 2) == hours;

    public static bool IsValidMultiplier(decimal multiplier) =>
        multiplier is >= MinMultiplier and <= MaxMultiplier && decimal.Round(multiplier, 2) == multiplier;

    public static bool IsValidHourlyRate(decimal rate) => rate > 0m && Money.Round2(rate) == rate && rate <= 999_999_999m;

    /// <summary>The payslip line of the overtime it takes: "Horas extra (7,5 h)".</summary>
    public static string Concept(decimal totalHours) =>
        $"Horas extra ({totalHours.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',')} h)";
}

/// <summary>Where an overtime record stands: not in a payroll yet, in a Draft run, or in a Paid one (final).</summary>
public static class OvertimeStatus
{
    public const string Pending = "Pending";
    public const string InPayroll = "InPayroll";
    public const string Paid = "Paid";
}
