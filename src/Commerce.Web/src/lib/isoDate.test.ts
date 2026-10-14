import { describe, expect, it } from 'vitest'
import { monthBeforeIso } from './isoDate'

describe('monthBeforeIso', () => {
  it('is the same day of the month before, or its last day when that month is shorter', () => {
    expect(monthBeforeIso('2026-10-14')).toBe('2026-09-14')
    expect(monthBeforeIso('2026-01-10')).toBe('2025-12-10')
    expect(monthBeforeIso('2026-03-31')).toBe('2026-02-28')
    expect(monthBeforeIso('2028-03-31')).toBe('2028-02-29')
    expect(monthBeforeIso('2026-10-31')).toBe('2026-09-30')
  })
})
