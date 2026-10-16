import { describe, expect, it } from 'vitest'
import { payPeriodStartIso } from './payPeriod'

// owner feedback 2026-10-15, B5: where an employee's current pay period starts, up to today (both days included).
describe('payPeriodStartIso', () => {
  it('starts a monthly period on the first day of the month', () => {
    expect(payPeriodStartIso('Monthly', '2026-10-15')).toBe('2026-10-01')
    expect(payPeriodStartIso('Monthly', '2026-10-01')).toBe('2026-10-01')
  })

  it('reads the last 15 days for fortnightly staff and the last 7 for weekly staff, today included', () => {
    expect(payPeriodStartIso('Biweekly', '2026-10-15')).toBe('2026-10-01')
    expect(payPeriodStartIso('Weekly', '2026-10-15')).toBe('2026-10-09')
    expect(payPeriodStartIso('Weekly', '2026-03-03')).toBe('2026-02-25')
  })
})
