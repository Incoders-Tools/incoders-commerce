import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ORGANIZATION_SUSPENDED_EVENT } from '@/api/client'
import { AuthProvider, hasPermission, useAuth } from './AuthContext'
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
    isSystemAdmin: false,
    selectableBranches: [],
  }
  const seller: SignedInResponse = {
    organizationId: 'org-1',
    userId: 'user-2',
    displayName: 'Sam',
    permissions: Permission.ViewSales,
    isSystemAdmin: false,
    selectableBranches: [],
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

/** organization-account-standing T7: a suspension noticed mid-session switches the session to Suspended. */
describe('AuthProvider and a mid-session suspension', () => {
  const fetchMock = vi.fn()
  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  function Probe() {
    const { user, signIn } = useAuth()
    return (
      <>
        <button type="button" onClick={() => void signIn({ email: 'a@example.com', password: 'p' })}>sign in</button>
        <p data-testid="status">{user?.accountStanding?.status ?? 'none'}</p>
        <p data-testid="suspendsOn">{user?.accountStanding?.suspendsOn ?? 'none'}</p>
      </>
    )
  }

  it('marks the signed-in session Suspended when the API announces it, keeping the known date', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
      organizationId: 'org-1', userId: 'user-1', displayName: 'Jane', permissions: Permission.ManageUsers,
      isSystemAdmin: false, selectableBranches: [],
      accountStanding: { status: 'Overdue', suspendsOn: '2026-11-08', daysLeft: 1 },
    }), { status: 200 }))
    render(<AuthProvider><Probe /></AuthProvider>)
    fireEvent.click(screen.getByRole('button', { name: 'sign in' }))
    await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('Overdue'))

    act(() => {
      window.dispatchEvent(new CustomEvent(ORGANIZATION_SUSPENDED_EVENT))
    })

    expect(screen.getByTestId('status')).toHaveTextContent('Suspended')
    expect(screen.getByTestId('suspendsOn')).toHaveTextContent('2026-11-08')
  })

  it('ignores the announcement when nobody is signed in', () => {
    render(<AuthProvider><Probe /></AuthProvider>)

    act(() => {
      window.dispatchEvent(new CustomEvent(ORGANIZATION_SUSPENDED_EVENT))
    })

    expect(screen.getByTestId('status')).toHaveTextContent('none')
  })
})
