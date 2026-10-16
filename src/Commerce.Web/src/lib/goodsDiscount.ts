// employee-lifecycle E4: the server's `PayslipPurchases` (Commerce.Domain/Payroll/Payslip.cs), mirrored so the payslip
// editor previews what the payroll will forgive and deduct. The server recomputes it; this only shows it beforehand.

/** To the cent, half away from zero (`Money.Round2`). */
const round2 = (value: number) => Math.sign(value) * Math.round((Math.abs(value) + Number.EPSILON) * 100) / 100

/** What an item's discount forgives: its percent of the item's total (`EmployeeGoodsRules.ItemDiscount`). */
export const itemDiscount = (lineTotal: number, percent: number) => round2((lineTotal * percent) / 100)

/**
 * The goods discount of a payslip: the item discounts first (never more than is owed), then the percent on the rest.
 * `deducted` is what comes off the salary.
 */
export function goodsDiscount(amount: number, percent: number, itemsDiscount: number) {
  const applied = Math.min(Math.max(itemsDiscount, 0), amount)
  const discount = round2(applied + round2(((amount - applied) * percent) / 100))
  return { itemsDiscount: applied, discount, deducted: round2(amount - discount) }
}
