import { describe, expect, it } from 'vitest'
import { formatStockQuantity, parseDecimal, unitLabel } from './quantity'

describe('parseDecimal', () => {
  it('reads a decimal comma, a decimal point and a thousands separator', () => {
    expect(parseDecimal('120')).toBe(120)
    expect(parseDecimal('2,5')).toBe(2.5)
    expect(parseDecimal('2.5')).toBe(2.5)
    expect(parseDecimal('4.000')).toBe(4000)
    expect(parseDecimal('1.234,56')).toBe(1234.56)
  })

  it('returns null for blank or non numeric text', () => {
    expect(parseDecimal('')).toBeNull()
    expect(parseDecimal('  ')).toBeNull()
    expect(parseDecimal('abc')).toBeNull()
  })
})

describe('formatStockQuantity', () => {
  it('words kilos for weighted and bulk, units for fixed quantity', () => {
    expect(unitLabel('Weighted')).toBe('kg')
    expect(unitLabel('Bulk')).toBe('kg')
    expect(unitLabel('FixedQuantity')).toBe('u')
  })

  it('shows up to three decimals in es-AR with the unit', () => {
    expect(formatStockQuantity(120, 'Weighted')).toBe('120 kg')
    expect(formatStockQuantity(2.5, 'Weighted')).toBe('2,5 kg')
    expect(formatStockQuantity(1234.5678, 'Bulk')).toBe('1.234,568 kg')
    expect(formatStockQuantity(12, 'FixedQuantity')).toBe('12 u')
    expect(formatStockQuantity(-3, 'FixedQuantity')).toBe('-3 u')
  })
})
