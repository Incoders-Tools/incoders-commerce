import { describe, expect, it } from 'vitest'
import {
  formatSignedQuantity,
  formatStockQuantity,
  parseAmount,
  parseQuantity,
  readQuantity,
  unitLabel,
} from './quantity'

describe('parseQuantity with a decimal comma', () => {
  it('reads a decimal comma and a thousands point', () => {
    expect(parseQuantity('120', 'Comma')).toBe(120)
    expect(parseQuantity('2,5', 'Comma')).toBe(2.5)
    expect(parseQuantity('4.000', 'Comma')).toBe(4000)
    expect(parseQuantity('1.234,56', 'Comma')).toBe(1234.56)
  })

  it('rejects a point used as decimal instead of silently reading it', () => {
    expect(parseQuantity('2.5', 'Comma')).toBeNull()
    expect(readQuantity('2.5', 'Comma')).toEqual({ ok: false, reason: 'separator' })
    expect(readQuantity('1.5000', 'Comma')).toEqual({ ok: false, reason: 'separator' })
  })
})

describe('parseQuantity with a decimal dot', () => {
  it('reads digits with one decimal point', () => {
    expect(parseQuantity('120', 'Dot')).toBe(120)
    expect(parseQuantity('1.5', 'Dot')).toBe(1.5)
    expect(parseQuantity('1.500', 'Dot')).toBe(1.5)
    expect(parseQuantity('0.125', 'Dot')).toBe(0.125)
  })

  it('rejects a comma (no thousands separator exists in this mode)', () => {
    expect(parseQuantity('1,5', 'Dot')).toBeNull()
    expect(parseQuantity('1,500', 'Dot')).toBeNull()
    expect(readQuantity('1,5', 'Dot')).toEqual({ ok: false, reason: 'separator' })
  })

  it('rejects more than one point', () => {
    expect(readQuantity('1.2.3', 'Dot')).toEqual({ ok: false, reason: 'format' })
  })
})

describe('readQuantity', () => {
  it('tells blank from unreadable text, never 0', () => {
    expect(readQuantity('', 'Comma')).toEqual({ ok: false, reason: 'blank' })
    expect(readQuantity('  ', 'Dot')).toEqual({ ok: false, reason: 'blank' })
    expect(readQuantity('abc', 'Dot')).toEqual({ ok: false, reason: 'format' })
    expect(parseQuantity('abc', 'Comma')).toBeNull()
  })
})

describe('parseAmount (money, always es-AR)', () => {
  it('keeps reading a decimal comma, a decimal point and a thousands separator', () => {
    expect(parseAmount('2,5')).toBe(2.5)
    expect(parseAmount('2.5')).toBe(2.5)
    expect(parseAmount('4.000')).toBe(4000)
    expect(parseAmount('1.234,56')).toBe(1234.56)
    expect(parseAmount('')).toBeNull()
  })
})

describe('formatStockQuantity', () => {
  it('words kilos for weighted and bulk, units for fixed quantity', () => {
    expect(unitLabel('Weighted')).toBe('kg')
    expect(unitLabel('Bulk')).toBe('kg')
    expect(unitLabel('FixedQuantity')).toBe('u')
  })

  it('shows up to three decimals with a comma and es-AR thousands for Comma', () => {
    expect(formatStockQuantity(120, 'Weighted', 'Comma')).toBe('120 kg')
    expect(formatStockQuantity(2.5, 'Weighted', 'Comma')).toBe('2,5 kg')
    expect(formatStockQuantity(1234.5678, 'Bulk', 'Comma')).toBe('1.234,568 kg')
    expect(formatStockQuantity(12, 'FixedQuantity', 'Comma')).toBe('12 u')
    expect(formatStockQuantity(-3, 'FixedQuantity', 'Comma')).toBe('-3 u')
  })

  it('shows a decimal point and no thousands separator for Dot, so it reads back as typed', () => {
    expect(formatStockQuantity(117.5, 'Weighted', 'Dot')).toBe('117.5 kg')
    expect(formatStockQuantity(1234.5678, 'Bulk', 'Dot')).toBe('1234.568 kg')
    expect(formatStockQuantity(12000, 'FixedQuantity', 'Dot')).toBe('12000 u')
    expect(parseQuantity('1234.568', 'Dot')).toBe(1234.568)
  })
})

describe('formatSignedQuantity', () => {
  it('signs movements and follows the separator', () => {
    expect(formatSignedQuantity(120, 'Comma')).toBe('+120')
    expect(formatSignedQuantity(-2.5, 'Comma')).toBe('-2,5')
    expect(formatSignedQuantity(-2.5, 'Dot')).toBe('-2.5')
    expect(formatSignedQuantity(0, 'Dot')).toBe('0')
  })
})
