import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { currentUser } from '@/api/account'
import type { SelectableBranch } from '@/api/types'
import { useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

/**
 * admin-console spec, "Top Navbar Branch Switcher"; organization-persistence
 * spec, "Selectable Branches In The Session"; platform-administration spec,
 * "Sysadmin Selects Any Branch Of The Selected Organization" (B7 U3).
 *
 * The branch a signed-in identity has selected. For a business-admin (or
 * any staff member) this comes straight from the signed-in `SignedInResponse`
 * — their own `BranchScope`, unaffected by any organization selector. For a
 * system administrator ACTING ON a selected organization
 * (`organization/OrganizationContext.tsx`), it comes from that organization's
 * branches instead (a sysadmin's own `BranchScope` is always empty), fetched
 * by re-reading `/account/me` with the organization header already attached
 * — the same endpoint sign-in itself uses, so the branch list is always
 * exactly what the server would compute for that selection, never a second
 * hand-rolled rule on the client. `/account/branches` was the other option
 * but it additionally requires `ManageBranchSettings`, which not every staff
 * member holds — `/account/me` has no such gate.
 */
export type SelectedBranch = SelectableBranch

interface BranchContextValue {
  selectedBranch: SelectedBranch | null
  selectableBranches: SelectedBranch[]
  selectBranch: (branch: SelectedBranch) => void
}

/**
 * Exported (unlike `OrganizationContext`'s private context) so a component
 * test can supply a value directly — the same direct-`Context.Provider`
 * pattern `OrganizationBrandingContext` already uses for `AppLayout.test.tsx`
 * — instead of driving the real provider through a network mock for every
 * `BranchSwitcher` case.
 */
export const BranchContext = createContext<BranchContextValue | undefined>(undefined)

function storageKeyFor(userId: string | undefined, organizationScopeId: string | null): string {
  return `branch:${userId ?? 'anonymous'}:${organizationScopeId ?? 'own'}`
}

function readLastUsedBranchId(storageKey: string): string | null {
  try {
    return window.localStorage.getItem(storageKey)
  } catch {
    // localStorage unavailable (private browsing, disabled storage, etc.).
    return null
  }
}

function writeLastUsedBranchId(storageKey: string, branchId: string): void {
  try {
    window.localStorage.setItem(storageKey, branchId)
  } catch {
    // In-memory selection still works for the current tab.
  }
}

/**
 * Auto-select rule (admin-console spec, "Top Navbar Branch Switcher"): a
 * single selectable branch is chosen automatically; otherwise the last used
 * branch wins if it is still selectable, otherwise the first listed branch.
 * An empty list selects nothing.
 */
function computeSelection(branches: SelectedBranch[], lastUsedId: string | null): SelectedBranch | null {
  if (branches.length === 0) return null
  if (branches.length === 1) return branches[0]
  if (lastUsedId) {
    const stillSelectable = branches.find((branch) => branch.id === lastUsedId)
    if (stillSelectable) return stillSelectable
  }
  return branches[0]
}

/**
 * Module-scoped mirror of the current selection, read by `apiFetch`
 * (`api/client.ts`) OUTSIDE React so every request can attach the
 * `X-Branch-Id` header without threading it through every call site — same
 * pattern as `OrganizationContext`'s `getSelectedOrganizationId`, for the
 * same reason: React runs a child's effects before its parent's, so an
 * effect-based sync would let a screen's on-mount fetch go out without the
 * header. Updated synchronously (never from inside a plain `useEffect`) on
 * every change: initial render, an identity/organization change, and an
 * explicit selection.
 */
let currentSelectedBranchId: string | null = null

export function getSelectedBranchId(): string | null {
  return currentSelectedBranchId
}

export function BranchProvider({ children }: { children: ReactNode }) {
  const auth = useOptionalAuth()
  const userId = auth?.user?.userId
  const isSystemAdmin = Boolean(auth?.user?.isSystemAdmin)
  const ownSelectableBranches = auth?.user?.selectableBranches ?? []

  // Non-throwing: a host that renders `BranchProvider` without an
  // `OrganizationProvider` mounted still renders — there is simply no
  // sysadmin organization selection to react to.
  const organizationContext = useOptionalOrganizationContext()
  const selectedOrganizationId = organizationContext?.selectedOrganization?.id ?? null
  const actingOnOrganization = isSystemAdmin && selectedOrganizationId !== null

  const storageKey = storageKeyFor(userId, selectedOrganizationId)

  // `null` means "not yet fetched for the current organization selection" —
  // distinct from an empty array, which means the fetch resolved to zero
  // branches. Irrelevant (and left `null`) whenever `actingOnOrganization`
  // is false, since `ownSelectableBranches` is used directly then.
  const [remoteBranches, setRemoteBranches] = useState<SelectedBranch[] | null>(null)

  const initialBranches = actingOnOrganization ? [] : ownSelectableBranches
  const [selectedBranch, setSelectedBranchState] = useState<SelectedBranch | null>(() => {
    const initial = computeSelection(initialBranches, readLastUsedBranchId(storageKey))
    currentSelectedBranchId = initial?.id ?? null
    return initial
  })

  // Render-time reset (React's documented "adjusting state when a prop
  // changes" pattern, not a `useEffect`) whenever the effective identity or
  // organization selection changes: the same synchronous-mirror discipline
  // `OrganizationContext` uses for the exact same reason — a selection that
  // is also immediately followed by mounting a new screen (the branch-keyed
  // `<Outlet>` in `AppLayout`) must never see a stale header, even for one
  // render.
  const trackedStorageKeyRef = useRef(storageKey)
  if (trackedStorageKeyRef.current !== storageKey) {
    trackedStorageKeyRef.current = storageKey
    setRemoteBranches(null)
    const branches = actingOnOrganization ? [] : ownSelectableBranches
    const next = computeSelection(branches, readLastUsedBranchId(storageKey))
    currentSelectedBranchId = next?.id ?? null
    setSelectedBranchState(next)
  }

  const effectiveBranches = actingOnOrganization ? remoteBranches ?? [] : ownSelectableBranches

  // Fetches the selected organization's branches for a sysadmin. Not an
  // effect-based header sync (that stays synchronous above) — this is the
  // one part that genuinely cannot be synchronous: it is a network call.
  useEffect(() => {
    if (!actingOnOrganization) return

    let cancelled = false
    currentUser()
      .then((response) => {
        if (cancelled) return
        setRemoteBranches(response.selectableBranches)
        const next = computeSelection(response.selectableBranches, readLastUsedBranchId(storageKey))
        currentSelectedBranchId = next?.id ?? null
        setSelectedBranchState(next)
      })
      .catch(() => {
        if (cancelled) return
        setRemoteBranches([])
        currentSelectedBranchId = null
        setSelectedBranchState(null)
      })

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [actingOnOrganization, storageKey])

  const selectBranch = (branch: SelectedBranch) => {
    writeLastUsedBranchId(storageKey, branch.id)
    currentSelectedBranchId = branch.id
    setSelectedBranchState(branch)
  }

  return (
    <BranchContext.Provider value={{ selectedBranch, selectableBranches: effectiveBranches, selectBranch }}>
      {children}
    </BranchContext.Provider>
  )
}

export function useBranchContext(): BranchContextValue {
  const context = useContext(BranchContext)
  if (!context) {
    throw new Error('useBranchContext must be used within a BranchProvider')
  }
  return context
}

/**
 * Non-throwing variant (mirrors `useOptionalOrganizationContext`): a
 * component/test that renders `AppLayout` without a `BranchProvider` mounted
 * still renders, with nothing branch-related shown.
 */
export function useOptionalBranchContext(): BranchContextValue | null {
  return useContext(BranchContext) ?? null
}
