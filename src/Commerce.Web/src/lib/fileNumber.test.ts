import { describe, expect, it } from 'vitest'
import { formatFileNumber } from './fileNumber'

/** employee-lifecycle E1: the legajo reads zero-padded to four digits, as `EmployeeFileNumber.Format` in the POS. */
describe('formatFileNumber', () => {
  it('pads to four digits and keeps a longer number whole', () => {
    expect(formatFileNumber(1)).toBe('0001')
    expect(formatFileNumber(7)).toBe('0007')
    expect(formatFileNumber(1234)).toBe('1234')
    expect(formatFileNumber(12345)).toBe('12345')
  })
})
