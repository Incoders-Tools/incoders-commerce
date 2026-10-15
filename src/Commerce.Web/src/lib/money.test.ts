import { describe, expect, it } from 'vitest'

import { formatMoney, parseMoney, readMoney } from './money'

describe('readMoney (one es-AR rule for every money field)', () => {
  it('reads a point followed by exactly three digits as thousands', () => {
    expect(readMoney('15.000')).toEqual({ ok: true, value: 15000 })
    expect(readMoney('1.234.567,50')).toEqual({ ok: true, value: 1234567.5 })
    expect(readMoney('15.000,5')).toEqual({ ok: true, value: 15000.5 })
  })

  it('reads a comma, or a point followed by one or two digits, as the decimal', () => {
    expect(readMoney('15,5')).toEqual({ ok: true, value: 15.5 })
    expect(readMoney('15,50')).toEqual({ ok: true, value: 15.5 })
    expect(readMoney('15.50')).toEqual({ ok: true, value: 15.5 })
    expect(readMoney('15.5')).toEqual({ ok: true, value: 15.5 })
    expect(readMoney('15000')).toEqual({ ok: true, value: 15000 })
    expect(readMoney('0')).toEqual({ ok: true, value: 0 })
  })

  it('accepts a leading currency sign and surrounding spaces', () => {
    expect(readMoney(' $ 15.000 ')).toEqual({ ok: true, value: 15000 })
    expect(readMoney('$15000,5')).toEqual({ ok: true, value: 15000.5 })
  })

  it('refuses anything else', () => {
    expect(readMoney('1,234.56')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('15.0000')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('1234.567')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('1.234.5')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('15,')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney(',5')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('-15')).toEqual({ ok: false, reason: 'format' })
    expect(readMoney('abc')).toEqual({ ok: false, reason: 'format' })
  })

  it('refuses more decimals than the field takes, and says so', () => {
    expect(readMoney('15,505')).toEqual({ ok: false, reason: 'decimals' })
    expect(readMoney('12,3456', 4)).toEqual({ ok: true, value: 12.3456 })
    expect(readMoney('12,34567', 4)).toEqual({ ok: false, reason: 'decimals' })
    // With four decimals allowed a point followed by three digits is still thousands.
    expect(readMoney('1.500', 4)).toEqual({ ok: true, value: 1500 })
  })

  it('tells blank apart', () => {
    expect(readMoney('')).toEqual({ ok: false, reason: 'blank' })
    expect(readMoney('  $ ')).toEqual({ ok: false, reason: 'blank' })
  })
})

describe('parseMoney', () => {
  it('is the number, or null when blank or unreadable (never 0)', () => {
    expect(parseMoney('15.000')).toBe(15000)
    expect(parseMoney('')).toBeNull()
    expect(parseMoney('1,234.56')).toBeNull()
  })
})

describe('formatMoney', () => {
  it('writes the amount as read, in es-AR with cents', () => {
    expect(formatMoney(15000).replace(/\s/g, ' ')).toBe('$ 15.000,00')
    expect(formatMoney(12.3456, 4).replace(/\s/g, ' ')).toBe('$ 12,3456')
  })
})
