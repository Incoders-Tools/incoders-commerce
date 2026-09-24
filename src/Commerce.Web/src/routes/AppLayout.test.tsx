import { cleanup, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router'
import { AuthContext } from '@/auth/AuthContext'
import { ThemeProvider } from '@/theme/ThemeProvider'
import { Permission, type SignedInResponse } from '@/api/types'
import { AppLayout } from './AppLayout'

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

function renderLayout(user: SignedInResponse) {
  return render(
    <MemoryRouter initialEntries={['/app/catalog']}>
      <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
        <ThemeProvider>
          <Routes>
            <Route path="/app" element={<AppLayout />}>
              <Route path="catalog" element={<div>Catalog content</div>} />
            </Route>
          </Routes>
        </ThemeProvider>
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
    expect(nav.getByRole('link', { name: /catalog/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /orders/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /customers/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /users/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /branches/i })).not.toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizations/i })).not.toBeInTheDocument()
    // Change password moved into the account menu, not the nav bar.
    expect(nav.queryByRole('link', { name: /change password/i })).not.toBeInTheDocument()
  })

  it('additionally shows Customers, Users, and Branches to a user with ManageUsers, but not Organizations', () => {
    renderLayout(buildUser({ permissions: Permission.ManageUsers }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /customers/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /users/i })).toBeInTheDocument()
    expect(nav.getByRole('link', { name: /branches/i })).toBeInTheDocument()
    expect(nav.queryByRole('link', { name: /organizations/i })).not.toBeInTheDocument()
  })

  it('shows Organizations to a system admin', () => {
    renderLayout(buildUser({ isSystemAdmin: true }))

    const nav = within(screen.getByRole('navigation'))
    expect(nav.getByRole('link', { name: /organizations/i })).toBeInTheDocument()
  })

  it('renders full-width content (no centered max-width column) alongside the sidebar', () => {
    const { container } = renderLayout(buildUser())

    expect(container.querySelector('.max-w-3xl')).not.toBeInTheDocument()
  })

  it('toggles the mobile sidebar via the hamburger button', async () => {
    const user = userEvent.setup()
    const { container } = renderLayout(buildUser())

    const toggle = screen.getByRole('button', { name: /toggle navigation/i })
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
})
