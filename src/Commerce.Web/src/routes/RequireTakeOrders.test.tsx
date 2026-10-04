import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { OrganizationProvider } from '@/organization/OrganizationContext'
import { Permission, type SignedInResponse } from '@/api/types'
import { RequireTakeOrders } from './RequireTakeOrders'

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

function renderGuarded(user: SignedInResponse) {
  return render(
    <MemoryRouter initialEntries={['/app/orders']}>
      <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
        <OrganizationProvider>
          <Routes>
            <Route element={<RequireTakeOrders />}>
              <Route path="/app/orders" element={<div>Order screen</div>} />
            </Route>
          </Routes>
        </OrganizationProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
  )
}

describe('RequireTakeOrders', () => {
  afterEach(() => window.localStorage.clear())

  it('lets a seller with TakeOrders in', () => {
    renderGuarded(buildUser({ permissions: Permission.ViewSales | Permission.TakeOrders }))

    expect(screen.getByText('Order screen')).toBeInTheDocument()
  })

  it('shows a no-access state to a user without TakeOrders', () => {
    renderGuarded(buildUser({ permissions: Permission.ViewSales }))

    expect(screen.queryByText('Order screen')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Tomar pedido' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Tu usuario no tiene permiso para tomar pedidos.')
  })

  it('lets a system administrator in only while acting on an organization', () => {
    const { unmount } = renderGuarded(buildUser({ isSystemAdmin: true }))
    expect(screen.queryByText('Order screen')).not.toBeInTheDocument()
    unmount()

    window.localStorage.setItem('sysadmin-organization:user-1', JSON.stringify({ id: 'org-a', name: 'Org A' }))
    renderGuarded(buildUser({ isSystemAdmin: true }))
    expect(screen.getByText('Order screen')).toBeInTheDocument()
  })
})
