import { describe, expect, it } from 'vitest'
import { applyPercent, differencePercent, round2 } from './priceEditing'

describe('round2', () => {
  it('rounds half away from zero, like Money.Round2', () => {
    expect(round2(1.005)).toBe(1.01)
    expect(round2(880.055)).toBe(880.06)
    expect(round2(-1.005)).toBe(-1.01)
    expect(round2(2.004)).toBe(2)
  })
})

describe('applyPercent', () => {
  it('raises and lowers a base price to the cent', () => {
    expect(applyPercent(800.05, 10)).toBe(880.06)
    expect(applyPercent(2500.55, 10)).toBe(2750.61)
    expect(applyPercent(1000, 10)).toBe(1100)
    expect(applyPercent(100, -5)).toBe(95)
    expect(applyPercent(19.99, -12.5)).toBe(17.49)
  })
})

describe('differencePercent', () => {
  it('is the change against the old price', () => {
    expect(differencePercent(1000, 1100)).toBeCloseTo(10)
    expect(differencePercent(100, 95)).toBeCloseTo(-5)
  })
})
