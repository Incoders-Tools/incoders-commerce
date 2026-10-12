/**
 * employee-lifecycle E2, the web side of `OvertimeRules`: a Sunday proposes x2, any other day x1, and the amount is
 * hours x the employee's overtime hourly rate x the multiplier. The server computes and freezes the real amount; this
 * only lets the form preview it.
 */
export function proposedMultiplier(isoDate: string): number {
  const [year, month, day] = isoDate.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day)).getUTCDay() === 0 ? 2 : 1
}

export function overtimeAmount(hours: number, hourlyRate: number, multiplier: number): number {
  return Math.round(hours * hourlyRate * multiplier * 100) / 100
}
