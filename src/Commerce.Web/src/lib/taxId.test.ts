import { describe, expect, it } from 'vitest'
import { isValidTaxId } from './taxId'

describe('isValidTaxId', () => {
  it('accepts anything when there is no tax id type', () => {
    expect(isValidTaxId('None', '')).toBe(true)
  })

  it('needs 11 digits for a CUIT or CUIL, ignoring separators', () => {
    expect(isValidTaxId('Cuit', '30-12345678-9')).toBe(true)
    expect(isValidTaxId('Cuil', '20 12345678 9')).toBe(true)
    expect(isValidTaxId('Cuit', '30-1234-9')).toBe(false)
  })

  it('needs 7 or 8 digits for a DNI', () => {
    expect(isValidTaxId('Dni', '12.345.678')).toBe(true)
    expect(isValidTaxId('Dni', '123456')).toBe(false)
  })
})
