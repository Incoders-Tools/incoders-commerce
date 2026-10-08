import { describe, expect, it } from 'vitest'
import { suggestNextIdentificationCode } from './nextIdentificationCode'

describe('suggestNextIdentificationCode', () => {
  it('proposes the code after the highest one, keeping its zero padding', () => {
    expect(suggestNextIdentificationCode(['001', '090', '045', null])).toEqual({ last: '090', next: '091' })
  })

  it('grows past the padding when the width runs out', () => {
    expect(suggestNextIdentificationCode(['998', '999'])).toEqual({ last: '999', next: '1000' })
  })

  it('ignores barcodes and codes with letters', () => {
    expect(suggestNextIdentificationCode(['7790001000012', 'ABC-1', '012'])).toEqual({ last: '012', next: '013' })
  })

  it('has nothing to propose without a numeric code', () => {
    expect(suggestNextIdentificationCode([])).toBeNull()
    expect(suggestNextIdentificationCode(['ABC', '7790001000012', undefined])).toBeNull()
  })
})
