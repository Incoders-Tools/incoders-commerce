using Commerce.Domain.Pricing;

namespace Commerce.Domain.Payroll;

/// <summary>
/// The final settlement of an employee given the leave (employee-lifecycle E3): a payroll run of that employee alone,
/// from the day after the last paid period that included it (or its hire date, or the 1st of the termination month) to
/// the termination date. The agreed salary is proposed in proportion to the days worked (owner decision 2026-10-12):
/// salary x days worked / days of the period (the termination month's days if monthly, 15 if biweekly, 7 if weekly).
/// The draft stays editable, as any payroll.
/// </summary>
public static class FinalSettlementRules
{
    public const int MaxReasonLength = 500;

    public static DateOnly PeriodFrom(DateOnly? lastPaidPeriodTo, DateOnly? hireDate, DateOnly terminationDate)
    {
        if (lastPaidPeriodTo is { } paid)
        {
            return paid.AddDays(1);
        }

        var monthStart = new DateOnly(terminationDate.Year, terminationDate.Month, 1);
        return hireDate is { } hired && hired > monthStart && hired <= terminationDate ? hired : monthStart;
    }

    /// <summary>The days from <paramref name="from"/> to the termination date, both counted; none when already paid past it.</summary>
    public static int DaysWorked(DateOnly from, DateOnly terminationDate) => Math.Max(terminationDate.DayNumber - from.DayNumber + 1, 0);

    public static int PeriodDays(string payFrequency, DateOnly terminationDate) => payFrequency switch
    {
        PayFrequency.Biweekly => 15,
        PayFrequency.Weekly => 7,
        _ => DateTime.DaysInMonth(terminationDate.Year, terminationDate.Month),
    };

    public static decimal ProportionalSalary(decimal baseSalary, string payFrequency, int daysWorked, DateOnly terminationDate) =>
        Money.Round2(baseSalary * daysWorked / PeriodDays(payFrequency, terminationDate));

    /// <summary>The payslip line of that salary: "Sueldo básico proporcional (10 de 31 días)".</summary>
    public static string Concept(int daysWorked, int periodDays) => $"Sueldo básico proporcional ({daysWorked} de {periodDays} días)";

    /// <summary>The run's note: "Liquidación final de Pérez, Juan (baja 10/10/2026: Renuncia)".</summary>
    public static string Note(string employeeName, DateOnly terminationDate, string? reason) =>
        $"Liquidación final de {employeeName} (baja {terminationDate:dd/MM/yyyy}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}")})";
}
