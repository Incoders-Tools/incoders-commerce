import type { SignedInResponse } from '@/api/types'

/**
 * Post-auth destination seam (design.md "Post-auth destination seam").
 * Satisfies the locked "one `/login` for everyone" decision: what renders
 * after sign-in depends on *who* authenticated, not on which URL reached
 * `/login`. Ignores its argument today because `SignedInResponse` carries no
 * role/account-type discriminator yet — this is a named seam for ADR-009
 * Phase D, not dead code.
 */
export function resolveLandingPath(_user: SignedInResponse): string {
  return '/app'
}
