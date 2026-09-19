import { describe, expect, it } from 'vitest'
import { hasPermission } from './AuthContext'
import { Permission } from '@/api/types'
import type { SignedInResponse } from '@/api/types'

/**
 * design.md "Web admin gating": permission checks read `user.permissions`
 * (server-derived), never a display-name or role-name string.
 */
describe('hasPermission', () => {
  const admin: SignedInResponse = {
    organizationId: 'org-1',
    userId: 'user-1',
    displayName: 'Jane',
    permissions: Permission.ViewSales | Permission.ManageUsers,
  }
  const seller: SignedInResponse = {
    organizationId: 'org-1',
    userId: 'user-2',
    displayName: 'Sam',
    permissions: Permission.ViewSales,
  }

  it('returns true when the user holds the requested bit', () => {
    expect(hasPermission(admin, Permission.ManageUsers)).toBe(true)
  })

  it('returns false when the user lacks the requested bit', () => {
    expect(hasPermission(seller, Permission.ManageUsers)).toBe(false)
  })

  it('returns false for a null user (default-deny)', () => {
    expect(hasPermission(null, Permission.ManageUsers)).toBe(false)
  })
})
