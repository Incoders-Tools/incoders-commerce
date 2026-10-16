import { describe, expect, it } from 'vitest'
import { formatInstantDate, formatInstantDateTime, formatIsoDate, formatMoney } from './format'

describe('ledger formatting', () => {
  it('formats money the es-AR way, always with cents', () => {
    expect(formatMoney(100000)).toMatch(/100\.000,00/)
    expect(formatMoney(-5.5)).toMatch(/5,50/)
  })

  it('formats an ISO date without timezone drift', () => {
    expect(formatIsoDate('2026-10-01')).toBe('01/10/2026')
    expect(formatIsoDate('2026-10-01T23:59:00Z')).toBe('01/10/2026')
  })

  it('reads a moment as dd/mm/aaaa (and hh:mm), with leading zeros and the whole year, whatever the browser', () => {
    // owner feedback P3: never "5/1/2026" nor "05/01/26".
    const moment = new Date(2026, 0, 5, 9, 7)
    expect(formatInstantDate(moment)).toBe('05/01/2026')
    expect(formatInstantDateTime(moment)).toBe('05/01/2026 09:07')
    expect(formatInstantDate(moment.toISOString())).toBe('05/01/2026')
    expect(formatInstantDate('not a date')).toBe('—')
  })
})
