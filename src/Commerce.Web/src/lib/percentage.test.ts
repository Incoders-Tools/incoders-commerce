import { describe, expect, it } from 'vitest'
import { parsePercentage } from './percentage'

/**
 * owner-requests-quick-adjustments Q3 review: the discount is typed as text and read like the POS reads it, so "7,5"
 * works whatever the browser language (a number input can drop a comma and send nothing). The 0-100 range stays the
 * server's rule.
 */
describe('parsePercentage', () => {
  it.each([
    ['', null],
    ['10', 10],
    ['7,5', 7.5],
    ['7.5', 7.5],
    [' 10 % ', 10],
    ['12,25%', 12.25],
  ])('reads %j as %j', (text, expected) => {
    expect(parsePercentage(text)).toEqual({ ok: true, value: expected })
  })

  it.each(['1,000', 'diez', '7,555', '1234', '-5', '%'])('refuses %j', (text) => {
    expect(parsePercentage(text)).toEqual({ ok: false })
  })
})
