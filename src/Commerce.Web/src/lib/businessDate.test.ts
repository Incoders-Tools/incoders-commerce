import { describe, expect, it } from 'vitest'
import { BUSINESS_TIME_ZONE, formatInstantDate } from './businessDate'

/**
 * organization-account-standing polish: a timestamp's calendar date in an explicit zone. The zone is a parameter so this
 * is testable on any machine: a test that relied on the system zone could never fail on one set to Argentina's time.
 */
describe('formatInstantDate', () => {
  // 02:00 UTC on 2026-10-10 is the 10th in Madrid but still the 9th, 23:00, in Buenos Aires.
  const instant = '2026-10-10T02:00:00Z'

  it('formats the date in the zone it is given', () => {
    expect(formatInstantDate(instant, 'Europe/Madrid')).toBe('10/10/2026')
    expect(formatInstantDate(instant, 'America/Argentina/Buenos_Aires')).toBe('09/10/2026')
  })

  it('uses the business zone (Buenos Aires, like the server IBusinessClock) by default', () => {
    expect(BUSINESS_TIME_ZONE).toBe('America/Argentina/Buenos_Aires')
    expect(formatInstantDate(instant)).toBe('09/10/2026')
  })
})
