import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { RequireAuth } from './RequireAuth'

/**
 * web-app-routing spec: "Guarded Staff Routes Redirect on Unauthenticated
 * Access" / "Unauthenticated deep link redirects to login". Exercised over
 * a route tree containing the guard, a guarded route, and `/login`, per
 * design.md's `RequireAuth` interface.
 */
function renderGuardedTree(initialEntry: string, authValue: { user: unknown }) {
  return render(
    <AuthContext.Provider value={authValue as never}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/login" element={<div>Login screen</div>} />
          <Route element={<RequireAuth />}>
            <Route path="/app/orders" element={<div>Orders content</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  )
}

describe('RequireAuth', () => {
  it('redirects an unauthenticated visitor to /login and renders no guarded content', () => {
    renderGuardedTree('/app/orders', { user: null })

    expect(screen.getByText('Login screen')).toBeInTheDocument()
    expect(screen.queryByText('Orders content')).not.toBeInTheDocument()
  })

  it('renders the guarded Outlet content when a user is authenticated', () => {
    renderGuardedTree('/app/orders', { user: { organizationId: 'org-1', userId: 'user-1', displayName: 'Jane' } })

    expect(screen.getByText('Orders content')).toBeInTheDocument()
    expect(screen.queryByText('Login screen')).not.toBeInTheDocument()
  })

  it('carries the originally-requested location in navigation state', () => {
    let capturedFrom: string | undefined
    render(
      <AuthContext.Provider value={{ user: null, error: null, signIn: vi.fn(), signOut: vi.fn() }}>
        <MemoryRouter initialEntries={['/app/orders']}>
          <Routes>
            <Route
              path="/login"
              element={<CaptureFromState onCapture={(from) => (capturedFrom = from)} />}
            />
            <Route element={<RequireAuth />}>
              <Route path="/app/orders" element={<div>Orders content</div>} />
            </Route>
          </Routes>
        </MemoryRouter>
      </AuthContext.Provider>,
    )

    expect(capturedFrom).toBe('/app/orders')
  })
})

function CaptureFromState({ onCapture }: { onCapture: (from: string) => void }) {
  const location = useLocation()
  const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname
  if (from) onCapture(from)
  return <div>Login screen</div>
}
