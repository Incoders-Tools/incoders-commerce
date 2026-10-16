import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { getOwnOrganizationBranding } from '@/api/account'
import type { OrganizationBranding } from '@/api/types'
import { useOptionalAuth } from '@/auth/AuthContext'

interface OrganizationBrandingContextValue {
  branding: OrganizationBranding | null
  loading: boolean
}

const DEFAULT_VALUE: OrganizationBrandingContextValue = { branding: null, loading: false }

export const OrganizationBrandingContext = createContext<OrganizationBrandingContextValue | undefined>(undefined)

/**
 * T6: fetches the signed-in user's own organization branding
 * (`GET /account/organization/branding`) once per effective identity, so
 * both the theme system (ThemeProvider's "custom" option) and the app
 * shell (logo) can consume the same result instead of each fetching it
 * separately. Re-fetches when the identity changes (sign-in as someone
 * else on the same device) and clears on sign-out. A system admin with no
 * tenant scope, a 403/404, or a network failure all resolve to
 * `branding: null` — the custom theme and the logo both simply become
 * unavailable, nothing crashes.
 */
export function OrganizationBrandingProvider({ children }: { children: ReactNode }) {
  const auth = useOptionalAuth()
  const userId = auth?.user?.userId

  const [branding, setBranding] = useState<OrganizationBranding | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!userId) {
      setBranding(null)
      setLoading(false)
      return
    }

    let cancelled = false
    setLoading(true)
    getOwnOrganizationBranding()
      .then((result) => {
        if (!cancelled) setBranding(result)
      })
      .catch(() => {
        if (!cancelled) setBranding(null)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [userId])

  return (
    <OrganizationBrandingContext.Provider value={{ branding, loading }}>
      {children}
    </OrganizationBrandingContext.Provider>
  )
}

/**
 * Non-throwing, like `useOptionalAuth()`: components that read branding
 * (ThemeProvider, ThemeSwitcher) must keep working in tests and contexts
 * that don't mount `OrganizationBrandingProvider`, falling back to "no
 * branding" instead of throwing.
 */
export function useOrganizationBranding(): OrganizationBrandingContextValue {
  return useContext(OrganizationBrandingContext) ?? DEFAULT_VALUE
}
