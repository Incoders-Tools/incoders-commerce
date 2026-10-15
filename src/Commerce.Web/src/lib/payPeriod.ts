import type { PayFrequency } from '@/api/employees'
import { toIsoDate } from './isoDate'

/** Days a fortnightly or weekly pay period spans, today included. */
const PERIOD_DAYS: Record<Exclude<PayFrequency, 'Monthly'>, number> = { Biweekly: 15, Weekly: 7 }

/**
 * owner feedback 2026-10-15, B5: where the employee's current pay period starts (`yyyy-MM-dd`) for `todayIso`: the first
 * day of the month for monthly staff, the last 15 days for fortnightly staff and the last 7 for weekly staff, today
 * included. Where the advances history opens.
 */
export function payPeriodStartIso(frequency: PayFrequency, todayIso: string): string {
  const [year, month, day] = todayIso.split('-').map(Number)
  if (frequency === 'Monthly') return toIsoDate(new Date(year, month - 1, 1))
  return toIsoDate(new Date(year, month - 1, day - (PERIOD_DAYS[frequency] - 1)))
}
