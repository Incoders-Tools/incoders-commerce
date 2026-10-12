import { describe, expect, it } from 'vitest'
import { overtimeAmount, proposedMultiplier } from './overtime'

/** employee-lifecycle E2: the form previews what the server computes (`OvertimeRules`). */
describe('overtime', () => {
  it('proposes x2 on a Sunday and x1 any other day', () => {
    expect(proposedMultiplier('2026-10-11')).toBe(2)
    expect(proposedMultiplier('2026-10-12')).toBe(1)
    expect(proposedMultiplier('2026-10-17')).toBe(1)
  })

  it('previews hours x rate x multiplier, rounded to cents', () => {
    expect(overtimeAmount(7.5, 2000, 2)).toBe(30000)
    expect(overtimeAmount(1, 3333.333, 1)).toBe(3333.33)
  })
})
