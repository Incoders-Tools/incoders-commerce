import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import * as accountApi from '@/api/account'
import { ORGANIZATION_SUSPENDED_EVENT } from '@/api/client'
import { todayIso } from '@/lib/isoDate'
import type { AccountStandingSummary, SignedInResponse, SignInRequest } from '@/api/types'
import { Permission } from '@/api/types'

interface AuthContextValue {
  user: SignedInResponse | null
  error: string | null
  signIn: (request: SignInRequest) => Promise<void>
  signOut: () => Promise<void>
  /**
   * organization-account-standing T7: re-reads the organization's standing from `/account/me` (open while suspended), so
   * a reactivation lifts the suspended screen without signing in again. Optional so test doubles may omit it.
   */
  refreshStanding?: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | undefined>(undefined)

/** Stamps a standing with the local date it was received on (see `AccountStandingSummary.receivedOn`). */
function received(standing: AccountStandingSummary | null | undefined): AccountStandingSummary | null {
  return standing ? { ...standing, receivedOn: todayIso() } : null
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<SignedInResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  // organization-account-standing T7: the organization was suspended while this session was open. The session is
  // the sign-in response, so it is updated here rather than re-fetched; the known suspension date is kept.
  useEffect(() => {
    const markSuspended = () =>
      setUser((current) =>
        current === null || current.accountStanding?.status === 'Suspended'
          ? current
          : {
              ...current,
              accountStanding: { status: 'Suspended', suspendsOn: current.accountStanding?.suspendsOn ?? null, daysLeft: null },
            },
      )
    window.addEventListener(ORGANIZATION_SUSPENDED_EVENT, markSuspended)
    return () => window.removeEventListener(ORGANIZATION_SUSPENDED_EVENT, markSuspended)
  }, [])

  const signIn = async (request: SignInRequest) => {
    setError(null)
    try {
      const signedIn = await accountApi.signIn(request)
      setUser({ ...signedIn, accountStanding: received(signedIn.accountStanding) })
    } catch (err) {
      setUser(null)
      setError(err instanceof Error ? err.message : 'Sign-in failed.')
      throw err
    }
  }

  const refreshStanding = async () => {
    const me = await accountApi.currentUser()
    setUser((current) => (current === null ? current : { ...current, accountStanding: received(me.accountStanding) }))
  }

  const signOut = async () => {
    try {
      await accountApi.signOut()
    } finally {
      setUser(null)
    }
  }

  return (
    <AuthContext.Provider value={{ user, error, signIn, signOut, refreshStanding }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return context
}

/**
 * Non-throwing variant of `useAuth()` (design.md "`/` for a signed-in
 * visitor"): returns `null` outside an `AuthProvider` instead of throwing,
 * so public routes like `HomeScreen` can render with zero dependency on
 * auth machinery — including with no provider mounted at all.
 */
export function useOptionalAuth(): AuthContextValue | null {
  return useContext(AuthContext) ?? null
}

/**
 * commerce-customer-identity "Web admin gating": permission checks read
 * `user.permissions` (server-derived on `/account/sign-in` and
 * `/account/me`), never a display-name or role-name string — `RequireAdmin`
 * and `AppLayout`'s nav both go through this single helper.
 */
export function hasPermission(user: SignedInResponse | null, permission: Permission): boolean {
  return user !== null && (user.permissions & permission) === permission
}
