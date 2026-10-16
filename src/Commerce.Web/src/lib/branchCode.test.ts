import { describe, expect, it } from 'vitest'
import { formatBranchCode } from './branchCode'

// Mirrors Commerce.Domain.Tenancy.BranchCode.Format(): at least two digits.
describe('formatBranchCode', () => {
  it.each([
    [1, '01'],
    [9, '09'],
    [10, '10'],
    [99, '99'],
    [100, '100'],
    [999, '999'],
  ])('formats %i as %s', (code, expected) => {
    expect(formatBranchCode(code)).toBe(expected)
  })
})
