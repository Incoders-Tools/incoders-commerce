import { describe, expect, it } from 'vitest'
import { lineTotal, receptionTotal, type LineDraft } from './ReceptionLinesEditor'

const draft = (quantity: string, unitCost: string): LineDraft => ({
  key: quantity + unitCost,
  presentation: null,
  quantity,
  unitCost,
  lotCode: '',
  expiresOn: '',
})

describe('lineTotal', () => {
  it('reads the quantity in the organization format and the unit cost as es-AR money', () => {
    expect(lineTotal(draft('1.5', '4.000'), 'Dot')).toBe(6000)
    expect(lineTotal(draft('1,5', '4.000'), 'Comma')).toBe(6000)
  })

  it('is 0 for a quantity written in the other format, instead of reading it as another number', () => {
    expect(lineTotal(draft('1.500', '1000'), 'Comma')).toBe(1500000) // thousands point: 1500 kg
    expect(lineTotal(draft('1.5', '1000'), 'Comma')).toBe(0)
    expect(lineTotal(draft('1,5', '1000'), 'Dot')).toBe(0)
  })

  it('sums the lines', () => {
    expect(receptionTotal([draft('1.5', '100'), draft('2', '50')], 'Dot')).toBe(250)
  })
})
