import { useEffect } from 'react'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router'
import { AuthContext } from '@/auth/AuthContext'
import { BranchProvider } from '@/branch/BranchContext'
import { OrganizationProvider } from '@/organization/OrganizationContext'
import { OrganizationBrandingContext } from '@/theme/OrganizationBrandingProvider'
import { ThemeProvider } from '@/theme/ThemeProvider'
import { Permission, type SignedInResponse } from '@/api/types'
import type { OrganizationBranding } from '@/api/types'
import { AppLayout } from './AppLayout'

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

function renderLayout(
  user: SignedInResponse,
  branding: OrganizationBranding = { logoUrl: null, primaryColor: null },
) {
  return render(
    <MemoryRouter initialEntries={['/app/catalog']}>
      <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
        <BranchProvider>
          <OrganizationBrandingContext.Provider value={{ branding, loading: false }}>
            <ThemeProvider>
              <Routes>
                <Route path="/app" element={<AppLayout />}>
                  <Route path="catalog" element={<div>Catalog content</div>} />
                </Route>
              </Routes>
            </ThemeProvider>
          </OrganizationBrandingContext.Provider>
        </BranchProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
  )
}

describe('AppLayout', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.classList.remove('dark')
  })

  afterEach(() => {
    cleanup()
    document.documentElement.classList.remove('dark')
  })

  it('shows only Catalog and Orders to a plain authenticated user', () => {
    renderLayout(buildUser())

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /catálogo/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /pedidos/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /clientes/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /usuarios/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /sucursales/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizaciones/i })).not.toBeInTheDocument()
    // Change password moved into the account menu, not the nav bar.
    expect(nav.queryByRole('link', { name: /cambiar contraseña/i })).not.toBeInTheDocument()
  })

  it('renders an identifying icon next to every visible nav link, without changing its accessible name', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers, isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    for (const name of ['Catálogo', 'Pedidos', 'Clientes', 'Usuarios', 'Sucursales', 'Listas de precios', 'Organizaciones']) {
      const link = nav.getByRole('link', { name })
      const icon = link.querySelector('svg')
      expect(icon).not.toBeNull()
      expect(icon).toHaveAttribute('aria-hidden', 'true')
    }
  })

  it('replaces the hand-drawn hamburger icon with a lucide Menu icon', () => {
    renderLayout(buildUser())

    const toggle = screen.getByRole('button', { name: /alternar navegación/i })
    const icon = toggle.querySelector('svg')
    expect(icon).not.toBeNull()
    expect(icon).toHaveAttribute('aria-hidden', 'true')
    // A generic "has an svg" check also passed against the old hand-drawn
    // icon — lucide-react stamps every icon with a `lucide` + `lucide-<name>`
    // class, so this is what actually proves it is the real Menu icon.
    expect(icon).toHaveClass('lucide', 'lucide-menu')
  })

  it('additionally shows Customers, Users, and Branches to a user with ManageUsers, but not Organizations', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /clientes/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /usuarios/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /sucursales/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizaciones/i })).not.toBeInTheDocument()
  })

  it('shows Organizations to a system admin', () => {
    renderLayout(buildUser({ isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /organizaciones/i })).toBeInTheDocument()
  })

  // B1 (odd/tasks/frontend-modernization.md, product review backlog): the
  // sysadmin is the platform owner, never an organization's business-admin
  // — after the seeding fix it holds ZERO permissions (permissions: 0 is
  // this test's default). platform-administration spec, "Sysadmin Acts On
  // A Selected Organization" (the audit-flagged clarification): with no
  // organization selected, a sysadmin sees ONLY Organizations and other
  // platform-level screens — Catalog and Orders are tenant modules too, so
  // they are hidden here just like the ManageUsers-gated screens. (Was:
  // Catalog/Orders stayed visible unconditionally; that changed once
  // "no selection = no tenant data" became an explicit spec requirement.)
  it('shows a system admin with no roles Organizations only, not the tenant-scoped screens', () => {
    renderLayout(buildUser({ isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /organizaciones/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /catálogo/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /pedidos/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /clientes/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /usuarios/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /sucursales/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /listas de precios/i })).not.toBeInTheDocument()
  })

  // Once the sysadmin has selected an organization (OrganizationProvider
  // state), every tenant module reappears, exactly like a business-admin's
  // nav.
  it('shows every tenant module to a system admin who has selected an organization', () => {
    window.localStorage.setItem(
      'sysadmin-organization:user-1',
      JSON.stringify({ id: 'org-target', name: 'Target Org' }),
    )

    render(
      <MemoryRouter initialEntries={['/app/catalog']}>
        <AuthContext.Provider value={{ user: buildUser({ isSystemAdmin: true }), error: null, signIn: async () => {}, signOut: async () => {} }}>
          <OrganizationBrandingContext.Provider value={{ branding: { logoUrl: null, primaryColor: null }, loading: false }}>
            <OrganizationProvider>
              <BranchProvider>
                <ThemeProvider>
                  <Routes>
                    <Route path="/app" element={<AppLayout />}>
                      <Route path="catalog" element={<div>Catalog content</div>} />
                    </Route>
                  </Routes>
                </ThemeProvider>
              </BranchProvider>
            </OrganizationProvider>
          </OrganizationBrandingContext.Provider>
        </AuthContext.Provider>
      </MemoryRouter>,
    )

    const nav = within(screen.getByRole('navigation'))
    for (const name of ['Catálogo', 'Pedidos', 'Clientes', 'Usuarios', 'Sucursales', 'Listas de precios', 'Organizaciones']) {
      expect(nav.getByRole('link', { name })).toBeInTheDocument()
    }
  })

  it('renders full-width content (no centered max-width column) alongside the sidebar', () => {
    const { container } = renderLayout(buildUser())

    expect(container.querySelector('.max-w-3xl')).not.toBeInTheDocument()
  })

  it('toggles the mobile sidebar via the hamburger button', async () => {
    const user = userEvent.setup()
    const { container } = renderLayout(buildUser())

    const toggle = screen.getByRole('button', { name: /alternar navegación/i })
    const aside = container.querySelector('aside')
    expect(aside).not.toBeNull()

    expect(aside!.className).toMatch(/-translate-x-full/)
    await user.click(toggle)
    expect(aside!.className).not.toMatch(/-translate-x-full/)
  })

  it('exposes the account menu trigger with the display name in the header', () => {
    renderLayout(buildUser())

    expect(screen.getByRole('button', { name: /ada lovelace/i })).toBeInTheDocument()
  })

  it('keeps the text brand when the organization has no logo', () => {
    renderLayout(buildUser())

    expect(screen.getByRole('heading', { name: 'Commerce' })).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('renders the organization logo in the brand spot when logoUrl is set', () => {
    renderLayout(buildUser(), { logoUrl: 'https://cdn.example.com/logo.png', primaryColor: null })

    const logo = screen.getByRole('img', { name: 'Logotipo de la organización' })
    expect(logo).toHaveAttribute('src', 'https://cdn.example.com/logo.png')
    expect(screen.queryByRole('heading', { name: 'Commerce' })).not.toBeInTheDocument()
  })

  it('falls back to the text brand when the logo fails to load', () => {
    renderLayout(buildUser(), { logoUrl: 'https://cdn.example.com/logo.png', primaryColor: null })

    const logo = screen.getByRole('img', { name: 'Logotipo de la organización' })
    fireEvent.error(logo)

    expect(screen.getByRole('heading', { name: 'Commerce' })).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  // admin-console spec, "Top Navbar Branch Switcher".
  it('shows a real branch switcher when several branches are selectable', () => {
    renderLayout(
      buildUser({
        selectableBranches: [
          { id: 'b1', name: 'Ruta 51' },
          { id: 'b2', name: 'Centro' },
        ],
      }),
    )

    const select = screen.getByRole('combobox', { name: 'Sucursal' })
    expect(select).toHaveValue('b1')
    expect(screen.getByRole('option', { name: 'Centro' })).toBeInTheDocument()
  })

  it('shows a static branch label instead of a dropdown for a single selectable branch', () => {
    renderLayout(buildUser({ selectableBranches: [{ id: 'b1', name: 'Ruta 51' }] }))

    expect(screen.getByText('Ruta 51')).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Sucursal' })).not.toBeInTheDocument()
  })

  it('shows nothing branch-related for a staff member with no selectable branch', () => {
    renderLayout(buildUser({ selectableBranches: [] }))

    expect(screen.queryByRole('combobox', { name: 'Sucursal' })).not.toBeInTheDocument()
    expect(screen.queryByText('Sucursal')).not.toBeInTheDocument()
  })

  it('groups sidebar navigation under section headings', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers, isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByText('Operación')).toBeInTheDocument()
    expect(nav.getByText('Administración')).toBeInTheDocument()
    expect(nav.getByText('Plataforma')).toBeInTheDocument()
  })

  // "On branch switch, screens must refetch" (tasks.md B7 U3): the routed
  // Outlet is keyed by organization+branch so switching remounts it, rather
  // than relying on every screen to re-run its own fetch effect correctly.
  it('remounts the routed content when the branch changes', async () => {
    const user = userEvent.setup()
    const mounts: number[] = []

    function TrackMount() {
      useEffect(() => {
        mounts.push(mounts.length + 1)
      }, [])
      return <div>Catalog content</div>
    }

    render(
      <MemoryRouter initialEntries={['/app/catalog']}>
        <AuthContext.Provider
          value={{
            user: buildUser({
              selectableBranches: [
                { id: 'b1', name: 'Ruta 51' },
                { id: 'b2', name: 'Centro' },
              ],
            }),
            error: null,
            signIn: async () => {},
            signOut: async () => {},
          }}
        >
          <BranchProvider>
            <OrganizationBrandingContext.Provider value={{ branding: { logoUrl: null, primaryColor: null }, loading: false }}>
              <ThemeProvider>
                <Routes>
                  <Route path="/app" element={<AppLayout />}>
                    <Route path="catalog" element={<TrackMount />} />
                  </Route>
                </Routes>
              </ThemeProvider>
            </OrganizationBrandingContext.Provider>
          </BranchProvider>
        </AuthContext.Provider>
      </MemoryRouter>,
    )

    expect(mounts).toHaveLength(1)

    await user.selectOptions(screen.getByRole('combobox', { name: 'Sucursal' }), 'b2')

    expect(mounts).toHaveLength(2)
  })
})
