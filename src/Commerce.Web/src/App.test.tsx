import type { ReactNode } from 'react'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Permission, type SignedInResponse } from '@/api/types'
import App from './App'

/**
 * Route-table tests that exercise the REAL `App.tsx` tree, not a hand-built
 * `<Routes>` copy of it. A screen test that mounts its own
 * `<Route path="/app/price-lists" …>` proves the screen renders under a
 * guard, but it cannot fail when `App.tsx` never mounts that path — which is
 * exactly how `PriceListsScreen` stayed unreachable from the running app
 * while its own spec file was green.
 *
 * `AuthProvider` is the only thing stubbed: the guards (`RequireAuth`,
 * `RequireAdmin`) and every screen stay real, so the assertions below are
 * about the shipped route table and the shipped guards.
 */
const signedInUser: { current: SignedInResponse | null } = { current: null }

vi.mock('@/auth/AuthContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/auth/AuthContext')>()
  return {
    ...actual,
    AuthProvider: ({ children }: { children: ReactNode }) => (
      <actual.AuthContext.Provider
        value={{ user: signedInUser.current, error: null, signIn: async () => {}, signOut: async () => {} }}
      >
        {children}
      </actual.AuthContext.Provider>
    ),
  }
})

function buildUser(overrides: Partial<SignedInResponse> = {}): SignedInResponse {
  return {
    organizationId: 'org-1',
    userId: 'user-1',
    displayName: 'Ada Lovelace',
    permissions: 0,
    isSystemAdmin: false,
    ...overrides,
  }
}

function renderAppAt(path: string, user: SignedInResponse | null) {
  signedInUser.current = user
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  )
}

describe('App route table', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    // Every screen reachable here lists something on mount; an empty
    // collection keeps these tests about routing, not about data.
    fetchMock.mockResolvedValue(new Response(JSON.stringify([]), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    signedInUser.current = null
    window.localStorage.clear()
  })

  it('mounts the price lists screen at /app/price-lists for an admin', async () => {
    renderAppAt('/app/price-lists', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Listas de precios' })).toBeInTheDocument()
  })

  it('links the price lists screen from the admin sidebar', async () => {
    renderAppAt('/app/price-lists', buildUser({ permissions: Permission.ManageUsers }))

    await screen.findByRole('heading', { name: 'Listas de precios' })
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    const links = nav.getAllByRole('link', { name: /listas de precios/i })
    expect(links).toHaveLength(1)
    expect(links[0]).toHaveAttribute('href', '/app/price-lists')
  })

  it('redirects a non-admin away from /app/price-lists to the catalog', async () => {
    renderAppAt('/app/price-lists', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    // "Price lists" is a real heading of a real screen in this same app (see
    // the admin case above), so its absence here is a fact about the guard.
    expect(screen.queryByRole('heading', { name: 'Listas de precios' })).not.toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.queryAllByRole('link', { name: /listas de precios/i })).toHaveLength(0)
  })

  it('sends a signed-out visitor from /app/price-lists to the login screen', async () => {
    renderAppAt('/app/price-lists', null)

    expect(await screen.findByRole('heading', { name: /iniciar sesión/i })).toBeInTheDocument()
  })

  // platform-administration spec: a sysadmin with no selected organization
  // sees only Organizations, so /app must not land them on a tenant screen
  // their nav hides and the API answers 403.
  it('lands a system administrator without a selected organization on Organizations', async () => {
    renderAppAt('/app', buildUser({ isSystemAdmin: true }))

    expect(await screen.findByRole('heading', { name: 'Organizaciones' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Catálogo' })).not.toBeInTheDocument()
  })

  it('lands a system administrator acting on an organization on the catalog', async () => {
    window.localStorage.setItem('sysadmin-organization:user-1', JSON.stringify({ id: 'org-a', name: 'Org A' }))

    renderAppAt('/app', buildUser({ isSystemAdmin: true }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })

  it('still lands staff on the catalog', async () => {
    renderAppAt('/app', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })
})
