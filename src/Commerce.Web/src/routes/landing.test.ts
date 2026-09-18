import { describe, expect, it } from 'vitest'
import type { SignedInResponse } from '@/api/types'
import { resolveLandingPath } from './landing'

/**
 * Named seam for ADR-009 Phase D (design.md "Post-auth destination seam"):
 * branches on *identity*, not on which URL reached `/login`. Only one
 * outcome exists today because `SignedInResponse` carries no role/account-
 * type discriminator yet.
 */
describe('resolveLandingPath', () => {
  it('returns the staff app destination for any signed-in user shape', () => {
    const user: SignedInResponse = { organizationId: 'org-1', userId: 'user-1', displayName: 'Jane Doe', permissions: 0 }
    expect(resolveLandingPath(user)).toBe('/app')
  })
})
