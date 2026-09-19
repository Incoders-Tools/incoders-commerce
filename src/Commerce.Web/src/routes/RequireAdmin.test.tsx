import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import { RequireAdmin } from './RequireAdmin'

/**
 * design.md "Web admin gating": `RequireAdmin` mirrors `RequireAuth`'s exact
 * shape, additionally requiring the `ManageUsers` bit. A denial redirects to
 * `/app/catalog` (not `/login` — this guard only ever runs nested under
 * `RequireAuth`, so reaching it at all means the visitor is signed in).
 */
function renderGuardedTree(authValue: { user: unknown }) {
  return render(
    <AuthContext.Provider value={authValue as never}>
      <MemoryRouter initialEntries={['/app/customers']}>
        <Routes>
          <Route path="/app/catalog" element={<div>Catalog content</div>} />
          <Route element={<RequireAdmin />}>
            <Route path="/app/customers" element={<div>Customers content</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  )
}

describe('RequireAdmin', () => {
  it('redirects a seller (no ManageUsers bit) to /app/catalog and renders no guarded content', () => {
    renderGuardedTree({ user: { organizationId: 'org-1', userId: 'user-1', displayName: 'Sam', permissions: Permission.ViewSales } })

    expect(screen.getByText('Catalog content')).toBeInTheDocument()
    expect(screen.queryByText('Customers content')).not.toBeInTheDocument()
  })

  it('renders the guarded Outlet content for a ManageUsers holder', () => {
    renderGuardedTree({
      user: {
        organizationId: 'org-1',
        userId: 'user-1',
        displayName: 'Jane',
        permissions: Permission.ManageUsers | Permission.ViewSales,
      },
    })

    expect(screen.getByText('Customers content')).toBeInTheDocument()
    expect(screen.queryByText('Catalog content')).not.toBeInTheDocument()
  })

  it('default-denies a null user', () => {
    renderGuardedTree({ user: null })

    expect(screen.getByText('Catalog content')).toBeInTheDocument()
    expect(screen.queryByText('Customers content')).not.toBeInTheDocument()
  })
})
