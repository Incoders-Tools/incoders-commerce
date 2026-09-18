import { createContext, useContext, useState, type ReactNode } from 'react'
import * as accountApi from '@/api/account'
import type { SignedInResponse, SignInRequest } from '@/api/types'

interface AuthContextValue {
  user: SignedInResponse | null
  error: string | null
  signIn: (request: SignInRequest) => Promise<void>
  signOut: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<SignedInResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const signIn = async (request: SignInRequest) => {
    setError(null)
    try {
      const signedIn = await accountApi.signIn(request)
      setUser(signedIn)
    } catch (err) {
      setUser(null)
      setError(err instanceof Error ? err.message : 'Sign-in failed.')
      throw err
    }
  }

  const signOut = async () => {
    try {
      await accountApi.signOut()
    } finally {
      setUser(null)
    }
  }

  return (
    <AuthContext.Provider value={{ user, error, signIn, signOut }}>
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
