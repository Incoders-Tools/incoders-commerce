import { describe, expect, it } from 'vitest'
import { formatIsoDate, formatMoney } from './format'

describe('ledger formatting', () => {
  it('formats money the es-AR way, always with cents', () => {
    expect(formatMoney(100000)).toMatch(/100\.000,00/)
    expect(formatMoney(-5.5)).toMatch(/5,50/)
  })

  it('formats an ISO date without timezone drift', () => {
    expect(formatIsoDate('2026-10-01')).toBe('01/10/2026')
    expect(formatIsoDate('2026-10-01T23:59:00Z')).toBe('01/10/2026')
  })
})
