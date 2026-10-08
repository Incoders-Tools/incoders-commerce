using Commerce.Domain.Pricing;

namespace Commerce.Domain.Payroll;

/// <summary>How often an employee is paid; the payroll of a period prepares the employees paid that often.</summary>
public static class PayFrequency
{
    public const string Monthly = "Monthly";
    public const string Biweekly = "Biweekly";
    public const string Weekly = "Weekly";

    public static bool IsValid(string? value) => value is Monthly or Biweekly or Weekly;
}

/// <summary>Whether a payslip line adds to the pay or takes from it.</summary>
public enum PayslipLineKind
{
    Earning,
    Deduction,
}

/// <summary>
/// Where a line comes from: the agreed salary, the advances still pending on the employee's account, or added by hand
/// (overtime, a bonus, the SAC, another deduction...).
/// </summary>
public enum PayslipLineSource
{
    BaseSalary,
    Advances,
    Manual,
}

public sealed record PayslipLine(PayslipLineKind Kind, PayslipLineSource Source, string Concept, decimal Amount);

/// <summary>
/// The goods an employee took on its linked customer account and the discount the owner grants on them at pay day:
/// <see cref="Deducted"/> comes off the salary, <see cref="Discount"/> is forgiven (a credit note on the customer account).
/// </summary>
public sealed record PayslipPurchases(decimal Amount, decimal DiscountPercent)
{
    public decimal Discount => Money.Round2(Amount * DiscountPercent / 100m);

    public decimal Deducted => Amount - Discount;
}

/// <summary>
/// One employee's pay for a period (INTERNAL payroll, PRD 9.19: no legal contributions): earnings minus deductions minus
/// the purchases to deduct. The net can be negative (the employee owes more than its pay): it is shown, and nothing is
/// paid out for it; the rest stays on the employee's account.
/// </summary>
public sealed record Payslip(IReadOnlyList<PayslipLine> Lines, PayslipPurchases Purchases)
{
    public decimal Earnings => Lines.Where(line => line.Kind == PayslipLineKind.Earning).Sum(line => line.Amount);

    /// <summary>Every deduction line (advances and manual ones), purchases apart.</summary>
    public decimal Deductions => Lines.Where(line => line.Kind == PayslipLineKind.Deduction).Sum(line => line.Amount);

    public decimal AdvancesDeducted => Lines.Where(line => line.Source == PayslipLineSource.Advances).Sum(line => line.Amount);

    /// <summary>Deductions that are not advances (the advances were already paid out and are on the account).</summary>
    public decimal OtherDeductions => Deductions - AdvancesDeducted;

    public decimal Net => Earnings - Deductions - Purchases.Deducted;

    /// <summary>What actually leaves the treasury: the net, never less than zero.</summary>
    public decimal NetPaid => Math.Max(Net, 0m);
}

/// <summary>The rules of a payslip line and of the purchases discount.</summary>
public static class PayslipRules
{
    public const int MaxConceptLength = 200;

    public static bool IsValidAmount(decimal amount) => amount > 0m && Money.Round2(amount) == amount && amount <= 999_999_999_999m;

    public static bool IsValidDiscountPercent(decimal percent) => percent is >= 0m and <= 100m && decimal.Round(percent, 2) == percent;

    public static bool IsValidConcept(string? concept) => concept?.Trim() is { Length: > 0 and <= MaxConceptLength };
}
