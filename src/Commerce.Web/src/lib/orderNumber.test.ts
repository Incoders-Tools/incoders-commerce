import { describe, expect, it } from 'vitest'
import { parseOrderNumber } from './orderNumber'

// Mirrors Commerce.Domain.Ordering.OrderNumber.TryParse(): `P{branch}-W-{sequence}`.
describe('parseOrderNumber', () => {
  it('splits a canonical number into its four explainable parts', () => {
    expect(parseOrderNumber('P01-W-37')).toEqual({ type: 'P', branch: '01', origin: 'W', sequence: '37' })
    expect(parseOrderNumber('P100-W-1')).toEqual({ type: 'P', branch: '100', origin: 'W', sequence: '1' })
  })

  it.each([undefined, null, '', 'nope', 'V01-C2-125', 'P1-W-37', 'P01-W-0', 'P01-W-037', 'P01-X-37', 'P01-W-', 'P0001-W-1'])(
    'rejects %s',
    (text) => {
      expect(parseOrderNumber(text)).toBeNull()
    },
  )
})
