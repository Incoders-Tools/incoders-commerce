import { describe, expect, it } from 'vitest'
import { normalizePostalCode } from './postalCode'

/** Mirrors src/Commerce.Domain/Geography/PostalCodeRules.cs. */
describe('normalizePostalCode', () => {
  it('accepts a 4-digit CP and a CPA, trimmed and upper-cased', () => {
    expect(normalizePostalCode('2000')).toBe('2000')
    expect(normalizePostalCode(' s2000abc ')).toBe('S2000ABC')
  })

  it('treats a blank value as no postal code', () => {
    expect(normalizePostalCode('   ')).toBe('')
  })

  it.each(['200', '20000', 'I2000ABC', 'O2000ABC', 'S2000AB', 'S200ABC', '2000-ABC'])('refuses %j', (value) =>
    expect(normalizePostalCode(value)).toBeNull(),
  )
})
