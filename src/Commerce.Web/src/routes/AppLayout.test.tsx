import { useEffect, useState } from 'react'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { useUnsavedChanges } from '@/components/layout/UnsavedChanges'
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

  it('shows Orders only to a user who may take orders', () => {
    renderLayout(buildUser({ permissions: Permission.ViewSales | Permission.TakeOrders }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: 'Pedidos' })).toHaveAttribute('href', '/app/orders')
    expect(nav.getByRole('link', { name: /catálogo/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /clientes/i })).not.toBeInTheDocument()
  })

  it('hides Orders from an admin without TakeOrders', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers }))

    expect(within(screen.getByRole('navigation')).queryByRole('link', { name: 'Pedidos' })).not.toBeInTheDocument()
  })

  it('shows only Catalog to a plain authenticated user', () => {
    renderLayout(buildUser())

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /catálogo/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /pedidos/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /clientes/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /proveedor/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /recepciones/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: 'Stock' })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /usuarios/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /sucursales/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizaciones/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /tablero/i })).not.toBeInTheDocument()
    // Change password moved into the account menu, not the nav bar.
    expect(nav.queryByRole('link', { name: /cambiar contraseña/i })).not.toBeInTheDocument()
  })

  it('renders an identifying icon next to every visible nav link, without changing its accessible name', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers | Permission.TakeOrders, isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    for (const name of ['Tablero', 'Catálogo', 'Pedidos', 'Clientes', 'Usuarios', 'Sucursales', 'Listas de precios', 'Organizaciones']) {
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
    expect(nav.getByRole('link', { name: 'Tipos de negocio' })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: 'Proveedores' })).toHaveAttribute('href', '/app/suppliers')
    expect(nav.getByRole('link', { name: 'Rubros de proveedor' })).toHaveAttribute('href', '/app/supplier-categories')
    expect(nav.getByRole('link', { name: 'Recepciones' })).toHaveAttribute('href', '/app/receptions')
    expect(nav.getByRole('link', { name: 'Stock' })).toHaveAttribute('href', '/app/stock')
    // Cities are a core catalog managed by the system administrator only.
    expect(nav.queryByRole('link', { name: 'Ciudades' })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizaciones/i })).not.toBeInTheDocument()
  })

  it('puts the dashboard first in the sidebar for an admin, and hides it from a system admin with no organization', () => {
    const { unmount } = renderLayout(buildUser({ permissions: Permission.ManageUsers }))

    const links = within(screen.getByRole('navigation')).getAllByRole('link')
    expect(links[0]).toHaveAccessibleName('Tablero')
    expect(links[0]).toHaveAttribute('href', '/app/dashboard')
    unmount()

    renderLayout(buildUser({ isSystemAdmin: true }))
    expect(within(screen.getByRole('navigation')).queryByRole('link', { name: /tablero/i })).not.toBeInTheDocument()
  })

  it('shows Organizations to a system admin', () => {
    renderLayout(buildUser({ isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /organizaciones/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: 'Ciudades' })).toHaveAttribute('href', '/app/cities')
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
    expect(nav.getByRole('link', { name: 'Ciudades' })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /catálogo/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /pedidos/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /clientes/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /proveedor/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /recepciones/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: 'Stock' })).not.toBeInTheDocument()
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
          { id: 'b1', name: 'Ruta 51', code: 1 },
          { id: 'b2', name: 'Centro', code: 2 },
        ],
      }),
    )

    const select = screen.getByRole('combobox', { name: 'Sucursal' })
    expect(select).toHaveValue('b1')
    expect(screen.getByRole('option', { name: '02 · Centro' })).toBeInTheDocument()
  })

  it('shows a static branch label instead of a dropdown for a single selectable branch', () => {
    renderLayout(buildUser({ selectableBranches: [{ id: 'b1', name: 'Ruta 51', code: 1 }] }))

    expect(screen.getByText('Ruta 51')).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Sucursal' })).not.toBeInTheDocument()
  })

  it('shows nothing branch-related for a staff member with no selectable branch', () => {
    renderLayout(buildUser({ selectableBranches: [] }))

    expect(screen.queryByRole('combobox', { name: 'Sucursal' })).not.toBeInTheDocument()
    expect(screen.queryByText('Sucursal')).not.toBeInTheDocument()
  })

  // operator-ux-adjustments T2: daily work first, rarely used lookup tables
  // and system administration at the end, platform last.
  function sidebarOutline() {
    const nav = screen.getByRole('navigation')
    return Array.from(nav.querySelectorAll('a, [data-nav-section-title]')).map((node) =>
      node.tagName === 'A' ? node.textContent : `# ${node.textContent}`,
    )
  }

  it('groups the sidebar into the agreed sections, in order', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers | Permission.TakeOrders, isSystemAdmin: true }))

    expect(sidebarOutline()).toEqual([
      'Tablero',
      '# Operación',
      'Catálogo',
      'Pedidos',
      '# Gestión',
      'Clientes',
      'Proveedores',
      'Listas de precios',
      '# Compras',
      'Recepciones',
      'Stock',
      '# Tablas auxiliares',
      'Categorías',
      'Rubros de proveedor',
      'Tipos de negocio',
      '# Sistema',
      'Usuarios',
      'Sucursales',
      'Configuración',
      '# Plataforma',
      'Organizaciones',
      'Ciudades',
    ])
    expect(within(screen.getByRole('navigation')).queryByText('Administración')).not.toBeInTheDocument()
  })

  it('keeps the visibility gates when regrouping: an admin without TakeOrders or sysadmin rights', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers }))

    expect(sidebarOutline()).toEqual([
      'Tablero',
      '# Operación',
      'Catálogo',
      '# Gestión',
      'Clientes',
      'Proveedores',
      'Listas de precios',
      '# Compras',
      'Recepciones',
      'Stock',
      '# Tablas auxiliares',
      'Categorías',
      'Rubros de proveedor',
      'Tipos de negocio',
      '# Sistema',
      'Usuarios',
      'Sucursales',
      'Configuración',
    ])
  })

  it('keeps the visibility gates when regrouping: a plain user and a sysadmin without an organization', () => {
    const { unmount } = renderLayout(buildUser())
    expect(sidebarOutline()).toEqual(['# Operación', 'Catálogo'])
    unmount()

    renderLayout(buildUser({ isSystemAdmin: true }))
    expect(sidebarOutline()).toEqual(['# Plataforma', 'Organizaciones', 'Ciudades'])
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
                { id: 'b1', name: 'Ruta 51', code: 1 },
                { id: 'b2', name: 'Centro', code: 2 },
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

  describe('unsaved changes guard', () => {
    // A screen with pending work: registers its message while dirty, and can become clean or close.
    function DirtyScreen() {
      const [dirty, setDirty] = useState(true)
      useUnsavedChanges(dirty ? 'Tenés 2 cambios sin publicar.' : null)
      return (
        <div>
          Editor content
          <button type="button" onClick={() => setDirty(false)}>
            Publicar
          </button>
        </div>
      )
    }

    function ClosableDirtyScreen() {
      const [open, setOpen] = useState(true)
      return (
        <div>
          {open && <DirtyScreen />}
          <button type="button" onClick={() => setOpen(false)}>
            Cerrar editor
          </button>
        </div>
      )
    }

    function CurrentPath() {
      return <output data-testid="path">{useLocation().pathname}</output>
    }

    function renderGuarded(screenElement = <DirtyScreen />, signOut: () => Promise<void> = async () => {}) {
      render(
        <MemoryRouter initialEntries={['/app/price-lists']}>
          <AuthContext.Provider
            value={{ user: buildUser({ permissions: Permission.ManageUsers }), error: null, signIn: async () => {}, signOut }}
          >
            <BranchProvider>
              <OrganizationBrandingContext.Provider value={{ branding: { logoUrl: null, primaryColor: null }, loading: false }}>
                <ThemeProvider>
                  <CurrentPath />
                  <Routes>
                    <Route path="/app" element={<AppLayout />}>
                      <Route path="price-lists" element={screenElement} />
                      <Route path="customers" element={<div>Customers content</div>} />
                      <Route path="password" element={<div>Password content</div>} />
                    </Route>
                  </Routes>
                </ThemeProvider>
              </OrganizationBrandingContext.Provider>
            </BranchProvider>
          </AuthContext.Provider>
        </MemoryRouter>,
      )
      return userEvent.setup()
    }

    const clientesLink = () => within(screen.getByRole('navigation')).getByRole('link', { name: 'Clientes' })

    it('a sidebar click with pending changes asks first and stays on "Seguir editando"', async () => {
      const user = renderGuarded()

      await user.click(clientesLink())

      const dialog = await screen.findByRole('dialog')
      expect(dialog).toHaveTextContent('Tenés 2 cambios sin publicar.')
      expect(screen.getByTestId('path')).toHaveTextContent('/app/price-lists')
      await user.click(within(dialog).getByRole('button', { name: 'Seguir editando' }))

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      expect(screen.getByTestId('path')).toHaveTextContent('/app/price-lists')
      expect(screen.getByText('Editor content')).toBeInTheDocument()
    })

    it('"Descartar" navigates to the clicked target', async () => {
      const user = renderGuarded()

      await user.click(clientesLink())
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Descartar' }))

      expect(await screen.findByText('Customers content')).toBeInTheDocument()
      expect(screen.getByTestId('path')).toHaveTextContent('/app/customers')
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })

    it('the account menu link is guarded too', async () => {
      const user = renderGuarded()

      await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
      await user.click(screen.getByRole('menuitem', { name: /cambiar contraseña/i }))

      const dialog = await screen.findByRole('dialog')
      expect(screen.getByTestId('path')).toHaveTextContent('/app/price-lists')
      await user.click(within(dialog).getByRole('button', { name: 'Descartar' }))
      expect(await screen.findByText('Password content')).toBeInTheDocument()
    })

    it('signing out with pending changes asks first', async () => {
      const signOut = vi.fn().mockResolvedValue(undefined)
      const user = renderGuarded(<DirtyScreen />, signOut)

      await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
      await user.click(screen.getByRole('menuitem', { name: /cerrar sesión/i }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Seguir editando' }))
      expect(signOut).not.toHaveBeenCalled()

      await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
      await user.click(screen.getByRole('menuitem', { name: /cerrar sesión/i }))
      await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Descartar' }))
      expect(signOut).toHaveBeenCalledTimes(1)
    })

    it('does not ask once the screen is clean', async () => {
      const user = renderGuarded()

      await user.click(screen.getByRole('button', { name: 'Publicar' }))
      await user.click(clientesLink())

      expect(await screen.findByText('Customers content')).toBeInTheDocument()
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })

    it('does not ask once the dirty screen unmounted', async () => {
      const user = renderGuarded(<ClosableDirtyScreen />)

      await user.click(screen.getByRole('button', { name: 'Cerrar editor' }))
      await user.click(clientesLink())

      expect(await screen.findByText('Customers content')).toBeInTheDocument()
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
  })
})
