import { useEffect, useState } from 'react'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { OrganizationProvider, useOrganizationContext } from '@/organization/OrganizationContext'
import type { SelectableBranch, SignedInResponse } from '@/api/types'
import * as accountApi from '@/api/account'
import { BranchProvider, getSelectedBranchId, useBranchContext } from './BranchContext'

// admin-console spec, "Top Navbar Branch Switcher" / organization-persistence
// spec, "Selectable Branches In The Session": auto-select rules, per-identity
// persistence, and the same synchronous-header-mirror discipline
// OrganizationContext.test.tsx pins for the organization selector — a child
// fetching on its FIRST effect must already see the selected branch.

function buildUser(overrides: Partial<SignedInResponse> = {}): SignedInResponse {
  return {
    organizationId: 'org-1',
    userId: 'user-1',
    displayName: 'Ada Lovelace',
    permissions: 0,
    isSystemAdmin: false,
    selectableBranches: [],
    ...overrides,
  }
}

function Harness({ user, children }: { user: SignedInResponse | null; children: React.ReactNode }) {
  return (
    <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
      <OrganizationProvider>
        <BranchProvider>{children}</BranchProvider>
      </OrganizationProvider>
    </AuthContext.Provider>
  )
}

function BranchProbe() {
  const { selectedBranch, selectableBranches, selectBranch } = useBranchContext()
  return (
    <div>
      <span data-testid="selected">{selectedBranch?.name ?? 'none'}</span>
      <span data-testid="count">{selectableBranches.length}</span>
      {selectableBranches.map((branch) => (
        <button key={branch.id} type="button" onClick={() => selectBranch(branch)}>
          {branch.name}
        </button>
      ))}
    </div>
  )
}

function FirstEffectProbe({ onFirstEffect }: { onFirstEffect: (id: string | null) => void }) {
  useEffect(() => {
    onFirstEffect(getSelectedBranchId())
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  return null
}

function SelectThenMount({ branch, onFirstEffect }: { branch: SelectableBranch; onFirstEffect: (id: string | null) => void }) {
  const { selectBranch } = useBranchContext()
  const [mounted, setMounted] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => {
          selectBranch(branch)
          setMounted(true)
        }}
      >
        Select
      </button>
      {mounted && <FirstEffectProbe onFirstEffect={onFirstEffect} />}
    </>
  )
}

afterEach(() => {
  window.localStorage.clear()
  vi.restoreAllMocks()
})

describe('BranchProvider auto-selection', () => {
  it('auto-selects the only selectable branch', () => {
    render(
      <Harness user={buildUser({ selectableBranches: [{ id: 'b1', name: 'Ruta 51' }] })}>
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('selected')).toHaveTextContent('Ruta 51')
  })

  it('restores the last used branch when it is still selectable', () => {
    window.localStorage.setItem('branch:user-1:own', 'b2')

    render(
      <Harness
        user={buildUser({
          selectableBranches: [
            { id: 'b1', name: 'Ruta 51' },
            { id: 'b2', name: 'Centro' },
          ],
        })}
      >
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('selected')).toHaveTextContent('Centro')
  })

  it('falls back to the first branch when the stored id is no longer selectable', () => {
    window.localStorage.setItem('branch:user-1:own', 'unknown-branch')

    render(
      <Harness
        user={buildUser({
          selectableBranches: [
            { id: 'b1', name: 'Ruta 51' },
            { id: 'b2', name: 'Centro' },
          ],
        })}
      >
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('selected')).toHaveTextContent('Ruta 51')
  })

  it('shows no selection for a staff member with no selectable branch', () => {
    render(
      <Harness user={buildUser({ selectableBranches: [] })}>
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('count')).toHaveTextContent('0')
    expect(screen.getByTestId('selected')).toHaveTextContent('none')
  })
})

describe('BranchProvider header mirror', () => {
  it('exposes the auto-selected branch to a child fetching on its first mount', () => {
    const seen: (string | null)[] = []

    render(
      <Harness user={buildUser({ selectableBranches: [{ id: 'b1', name: 'Ruta 51' }] })}>
        <FirstEffectProbe onFirstEffect={(id) => seen.push(id)} />
      </Harness>,
    )

    expect(seen).toEqual(['b1'])
  })

  it('exposes a newly selected branch to a screen mounted in the same update', async () => {
    const seen: (string | null)[] = []

    render(
      <Harness
        user={buildUser({
          selectableBranches: [
            { id: 'b1', name: 'Ruta 51' },
            { id: 'b2', name: 'Centro' },
          ],
        })}
      >
        <SelectThenMount branch={{ id: 'b2', name: 'Centro' }} onFirstEffect={(id) => seen.push(id)} />
      </Harness>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Select' }))

    expect(seen).toEqual(['b2'])
  })
})

describe('BranchProvider switching', () => {
  it('updates the selection and persists it as the last used branch', async () => {
    const user = userEvent.setup()
    render(
      <Harness
        user={buildUser({
          selectableBranches: [
            { id: 'b1', name: 'Ruta 51' },
            { id: 'b2', name: 'Centro' },
          ],
        })}
      >
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('selected')).toHaveTextContent('Ruta 51')
    await user.click(screen.getByRole('button', { name: 'Centro' }))
    expect(screen.getByTestId('selected')).toHaveTextContent('Centro')
    expect(getSelectedBranchId()).toBe('b2')
    expect(window.localStorage.getItem('branch:user-1:own')).toBe('b2')
  })
})

describe('BranchProvider for a sysadmin acting on a selected organization', () => {
  it('refreshes selectable branches from the selected organization', async () => {
    vi.spyOn(accountApi, 'currentUser').mockResolvedValue(
      buildUser({
        isSystemAdmin: true,
        selectableBranches: [{ id: 'org-branch-1', name: 'Sucursal Norte' }],
      }),
    )
    window.localStorage.setItem(
      'sysadmin-organization:sysadmin-1',
      JSON.stringify({ id: 'org-target', name: 'Target Org' }),
    )

    render(
      <Harness user={buildUser({ userId: 'sysadmin-1', isSystemAdmin: true, selectableBranches: [] })}>
        <BranchProbe />
      </Harness>,
    )

    await waitFor(() => expect(screen.getByTestId('selected')).toHaveTextContent('Sucursal Norte'))
    expect(accountApi.currentUser).toHaveBeenCalled()
  })

  it('shows nothing selectable for a sysadmin with no organization selected', () => {
    render(
      <Harness user={buildUser({ userId: 'sysadmin-1', isSystemAdmin: true, selectableBranches: [] })}>
        <BranchProbe />
      </Harness>,
    )

    expect(screen.getByTestId('count')).toHaveTextContent('0')
  })

  it('clears the previous organization branch selection once the organization changes', async () => {
    const listForOrgTarget = [{ id: 'org-branch-1', name: 'Sucursal Norte' }]
    const listForOrgOther = [{ id: 'org-branch-2', name: 'Sucursal Sur' }]
    vi.spyOn(accountApi, 'currentUser').mockImplementation(async () => {
      const stored = window.localStorage.getItem('sysadmin-organization:sysadmin-1')
      const orgId = stored ? (JSON.parse(stored) as { id: string }).id : null
      return buildUser({
        isSystemAdmin: true,
        selectableBranches: orgId === 'org-target' ? listForOrgTarget : listForOrgOther,
      })
    })

    function Selector() {
      const { selectOrganization } = useOrganizationContext()
      return (
        <button type="button" onClick={() => selectOrganization({ id: 'org-other', name: 'Other Org' })}>
          Switch organization
        </button>
      )
    }

    window.localStorage.setItem(
      'sysadmin-organization:sysadmin-1',
      JSON.stringify({ id: 'org-target', name: 'Target Org' }),
    )

    render(
      <Harness user={buildUser({ userId: 'sysadmin-1', isSystemAdmin: true, selectableBranches: [] })}>
        <BranchProbe />
        <Selector />
      </Harness>,
    )

    await waitFor(() => expect(screen.getByTestId('selected')).toHaveTextContent('Sucursal Norte'))

    await act(async () => {
      screen.getByRole('button', { name: 'Switch organization' }).click()
    })

    await waitFor(() => expect(screen.getByTestId('selected')).toHaveTextContent('Sucursal Sur'))
  })
})
