using System.Globalization;

namespace Commerce.Domain.Payroll;

/// <summary>
/// The legajo: one number per organization (so it survives a branch change), read zero-padded to four digits ("0007") by
/// the web and the POS alike; a longer one reads whole (employee-lifecycle E1).
/// </summary>
public static class EmployeeFileNumber
{
    public static string Format(int fileNumber) => fileNumber.ToString("D4", CultureInfo.InvariantCulture);
}
