namespace Commerce.Domain.Payroll;

/// <summary>
/// owner feedback round 2, R3: what an employee has to collect to date, as the staff list reads it: the agreed salary
/// (while active) plus the overtime to pay, less what the employee owes. It is monthly: a paid payroll settles the
/// salary, the advances, the overtime and the goods, so it is back to the salary; whatever no payroll settled carries
/// over.
/// </summary>
public static class EmployeePayRules
{
    /// <param name="balance">The employee's account: negative when it owes (advances), positive when a salary was left owed.</param>
    /// <param name="goodsOwed">What its linked customer owes for goods (the next payroll deducts it).</param>
    public static decimal PayToDate(decimal baseSalary, decimal balance, decimal overtimeToPay, decimal goodsOwed, bool isActive) =>
        (isActive ? baseSalary : 0m) + balance + overtimeToPay - goodsOwed;
}
