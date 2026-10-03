import type { ReactNode } from 'react'
import { render, screen, waitFor, within } from '@testing-library/react'
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
    selectableBranches: [],
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

  it('mounts the categories screen at /app/categories for an admin and links it from the sidebar', async () => {
    renderAppAt('/app/categories', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Categorías' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    const links = nav.getAllByRole('link', { name: /categorías/i })
    expect(links).toHaveLength(1)
    expect(links[0]).toHaveAttribute('href', '/app/categories')
  })

  it('redirects a non-admin away from /app/categories to the catalog', async () => {
    renderAppAt('/app/categories', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Categorías' })).not.toBeInTheDocument()
  })

  it('mounts the business types screen for an admin and links it from the sidebar', async () => {
    renderAppAt('/app/business-types', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Tipos de negocio' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Tipos de negocio' })).toHaveAttribute('href', '/app/business-types')
  })

  it('mounts the suppliers screen for an admin and links it from the sidebar', async () => {
    renderAppAt('/app/suppliers', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Proveedores' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Proveedores' })).toHaveAttribute('href', '/app/suppliers')
  })

  it('mounts the supplier categories screen for an admin and links it from the sidebar', async () => {
    renderAppAt('/app/supplier-categories', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Rubros de proveedor' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Rubros de proveedor' })).toHaveAttribute('href', '/app/supplier-categories')
  })

  it('mounts the supplier current account at /app/suppliers/:id/account for an admin', async () => {
    renderAppAt('/app/suppliers/s-1/account', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('button', { name: 'Volver a proveedores' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /cuenta corriente/i })).toBeInTheDocument()
  })

  it('redirects a non-admin away from a supplier current account', async () => {
    renderAppAt('/app/suppliers/s-1/account', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /cuenta corriente/i })).not.toBeInTheDocument()
  })

  it('redirects a non-admin away from the supplier routes', async () => {
    const { unmount } = renderAppAt('/app/suppliers', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Proveedores' })).not.toBeInTheDocument()
    unmount()

    renderAppAt('/app/supplier-categories', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })

  it('mounts the receptions list and the reception form for an admin and links them from the sidebar', async () => {
    const { unmount } = renderAppAt('/app/receptions', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Recepciones' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Recepciones' })).toHaveAttribute('href', '/app/receptions')
    unmount()

    renderAppAt('/app/receptions/new', buildUser({ permissions: Permission.ManageUsers }))
    expect(await screen.findByRole('heading', { name: 'Nueva recepción' })).toBeInTheDocument()
  })

  it('redirects a non-admin away from the reception routes', async () => {
    const { unmount } = renderAppAt('/app/receptions', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Recepciones' })).not.toBeInTheDocument()
    unmount()

    renderAppAt('/app/receptions/new', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })

  it('mounts the stock screen and its movements for an admin and links it from the sidebar', async () => {
    const { unmount } = renderAppAt('/app/stock', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Stock' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Stock' })).toHaveAttribute('href', '/app/stock')
    unmount()

    renderAppAt('/app/stock/pr-1/movements', buildUser({ permissions: Permission.ManageUsers }))
    expect(await screen.findByRole('heading', { name: 'Movimientos de stock' })).toBeInTheDocument()
  })

  it('redirects a non-admin away from the stock routes', async () => {
    const { unmount } = renderAppAt('/app/stock', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Stock' })).not.toBeInTheDocument()
    unmount()

    renderAppAt('/app/stock/pr-1/movements', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })

  it('mounts the core cities screen for a system admin and links it from the sidebar', async () => {
    renderAppAt('/app/cities', buildUser({ isSystemAdmin: true }))

    expect(await screen.findByRole('heading', { name: 'Ciudades' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    expect(nav.getByRole('link', { name: 'Ciudades' })).toHaveAttribute('href', '/app/cities')
  })

  it('keeps the cities screen away from a business admin and a non-admin, who are sent to their landing screen', async () => {
    const { unmount } = renderAppAt('/app/cities', buildUser({ permissions: Permission.ManageUsers }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Ciudades' })).not.toBeInTheDocument()
    unmount()

    renderAppAt('/app/cities', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })

  it('redirects a non-admin away from the business types route', async () => {
    renderAppAt('/app/business-types', buildUser({ permissions: Permission.ViewSales }))
    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
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

  it('lands a system administrator acting on an organization on the dashboard', async () => {
    window.localStorage.setItem('sysadmin-organization:user-1', JSON.stringify({ id: 'org-a', name: 'Org A' }))

    renderAppAt('/app', buildUser({ isSystemAdmin: true }))

    expect(await screen.findByRole('heading', { name: 'Tablero' })).toBeInTheDocument()
  })

  it('lands a business admin on the dashboard', async () => {
    renderAppAt('/app', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Tablero' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Catálogo' })).not.toBeInTheDocument()
  })

  it('mounts the dashboard at /app/dashboard for an admin and links it first from the sidebar', async () => {
    renderAppAt('/app/dashboard', buildUser({ permissions: Permission.ManageUsers }))

    expect(await screen.findByRole('heading', { name: 'Tablero' })).toBeInTheDocument()
    const nav = within(screen.getByRole('navigation', { name: 'Principal' }))
    const links = nav.getAllByRole('link')
    expect(links[0]).toHaveAttribute('href', '/app/dashboard')
    expect(links[0]).toHaveAccessibleName('Tablero')
  })

  it('redirects a non-admin away from /app/dashboard to the catalog', async () => {
    renderAppAt('/app/dashboard', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Tablero' })).not.toBeInTheDocument()
  })

  it('still lands staff on the catalog', async () => {
    renderAppAt('/app', buildUser({ permissions: Permission.ViewSales }))

    expect(await screen.findByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
  })
})

/**
 * admin-console spec, "Top Navbar Branch Switcher". `BranchProvider` once
 * existed only in tests, so the shipped tree never sent `X-Branch-Id` and
 * every branch-owned endpoint answered 400 `branch-selection-required`.
 * These cases go through the real `App.tsx`, so they fail if it stops
 * mounting the provider.
 */
describe('App branch selection', () => {
  const fetchMock = vi.fn()
  const ruta51 = { id: 'b-ruta-51', name: 'Ruta 51', code: 1 }
  const centro = { id: 'b-centro', name: 'Centro', code: 2 }

  beforeEach(() => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify([]), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    signedInUser.current = null
    window.localStorage.clear()
  })

  function branchHeaderOf(path: string): string | undefined {
    const call = fetchMock.mock.calls.find(([url]) => String(url).startsWith(path))
    const headers = (call?.[1] as RequestInit | undefined)?.headers as Record<string, string> | undefined
    return headers?.['X-Branch-Id']
  }

  it('sends the auto-selected branch on a branch-owned request and shows it in the navbar', async () => {
    renderAppAt('/app/price-lists', buildUser({ permissions: Permission.ManageUsers, selectableBranches: [ruta51] }))

    expect(await screen.findByRole('heading', { name: 'Listas de precios' })).toBeInTheDocument()
    await waitFor(() => expect(branchHeaderOf('/pricing/price-lists')).toBe(ruta51.id))
    const header = within(screen.getByRole('banner'))
    expect(header.getByText('Ruta 51')).toBeInTheDocument()
  })

  it('offers the branch switcher when several branches are selectable and scopes requests to the selection', async () => {
    window.localStorage.setItem('branch:user-1:own', centro.id)

    renderAppAt('/app/stock', buildUser({ permissions: Permission.ManageUsers, selectableBranches: [ruta51, centro] }))

    expect(await screen.findByRole('heading', { name: 'Stock' })).toBeInTheDocument()
    await waitFor(() => expect(branchHeaderOf('/stock')).toBe(centro.id))
    expect(screen.getByRole('combobox', { name: 'Sucursal' })).toHaveValue(centro.id)
  })

  it('shows the select-a-branch state instead of calling a branch-owned endpoint when no branch is selectable', async () => {
    renderAppAt('/app/stock', buildUser({ permissions: Permission.ManageUsers, selectableBranches: [] }))

    expect(await screen.findByRole('status')).toHaveTextContent('Elegí una sucursal en la barra superior para ver el stock.')
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith('/stock'))).toBe(false)
  })
})
