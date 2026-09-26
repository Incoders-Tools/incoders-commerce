import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { useOptionalAuth } from '@/auth/AuthContext'

/**
 * platform-administration spec, "Sysadmin Acts On A Selected Organization":
 * the organization a system administrator has chosen to act on. Visible and
 * settable only through the sysadmin-only switcher in `AppLayout` — a
 * business-admin never reads or writes this context.
 */
export interface SelectedOrganization {
  id: string
  name: string
}

interface OrganizationContextValue {
  selectedOrganization: SelectedOrganization | null
  selectOrganization: (organization: SelectedOrganization) => void
  clearOrganization: () => void
}

const OrganizationContext = createContext<OrganizationContextValue | undefined>(undefined)

function storageKeyFor(userId: string | undefined): string {
  return `sysadmin-organization:${userId ?? 'anonymous'}`
}

function readStored(storageKey: string): SelectedOrganization | null {
  try {
    const raw = window.localStorage.getItem(storageKey)
    if (!raw) return null
    const parsed = JSON.parse(raw) as unknown
    if (
      typeof parsed === 'object' && parsed !== null &&
      typeof (parsed as SelectedOrganization).id === 'string' &&
      typeof (parsed as SelectedOrganization).name === 'string'
    ) {
      return parsed as SelectedOrganization
    }
    return null
  } catch {
    // localStorage unavailable (private browsing, disabled storage, etc.)
    // — fall back to "no organization selected" instead of throwing.
    return null
  }
}

function writeStored(storageKey: string, value: SelectedOrganization | null): void {
  try {
    if (value === null) {
      window.localStorage.removeItem(storageKey)
    } else {
      window.localStorage.setItem(storageKey, JSON.stringify(value))
    }
  } catch {
    // localStorage unavailable — the in-memory selection still works for
    // the current tab, it just will not survive a reload.
  }
}

/**
 * Module-scoped mirror of the current selection, read by `apiFetch`
 * (`api/client.ts`) OUTSIDE React so every request can attach the
 * `X-Organization-Id` header without threading it through every API call
 * site. Kept in sync by `OrganizationProvider` alone.
 */
let currentSelectedOrganizationId: string | null = null

export function getSelectedOrganizationId(): string | null {
  return currentSelectedOrganizationId
}

export function OrganizationProvider({ children }: { children: ReactNode }) {
  const auth = useOptionalAuth()
  const userId = auth?.user?.userId
  const storageKey = storageKeyFor(userId)

  const [selectedOrganization, setSelectedOrganization] = useState<SelectedOrganization | null>(() =>
    readStored(storageKey),
  )

  // Re-read (or clear) the persisted selection whenever the effective
  // identity changes — sign-in, sign-out, or switching accounts on the same
  // device — so a selection never leaks from one signed-in identity to
  // another sharing the same browser.
  useEffect(() => {
    setSelectedOrganization(readStored(storageKey))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey])

  useEffect(() => {
    currentSelectedOrganizationId = selectedOrganization?.id ?? null
  }, [selectedOrganization])

  const selectOrganization = (organization: SelectedOrganization) => {
    writeStored(storageKey, organization)
    setSelectedOrganization(organization)
  }

  const clearOrganization = () => {
    writeStored(storageKey, null)
    setSelectedOrganization(null)
  }

  return (
    <OrganizationContext.Provider value={{ selectedOrganization, selectOrganization, clearOrganization }}>
      {children}
    </OrganizationContext.Provider>
  )
}

export function useOrganizationContext(): OrganizationContextValue {
  const context = useContext(OrganizationContext)
  if (!context) {
    throw new Error('useOrganizationContext must be used within an OrganizationProvider')
  }
  return context
}

/**
 * Non-throwing variant (mirrors `useOptionalAuth`): returns `null` outside
 * an `OrganizationProvider` so a guard/nav component under test with no
 * provider mounted (e.g. `RequireAdmin.test.tsx`) still renders instead of
 * throwing. Every caller treats a `null` selection the same as "no
 * organization selected".
 */
export function useOptionalOrganizationContext(): OrganizationContextValue | null {
  return useContext(OrganizationContext) ?? null
}
