import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/auth/AuthContext'
import { RequireAuth } from './RequireAuth'
import { LoginRoute } from './LoginRoute'

/**
 * web-app-routing spec: "Post-login return to originally-requested route".
 * Exercises the real guard + login route pair with a mocked `fetch`, since
 * `AuthProvider`'s `signIn` calls the real `accountApi.signIn` -> `fetch`.
 */
function renderTree(initialEntry: string) {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/login" element={<LoginRoute />} />
          <Route element={<RequireAuth />}>
            <Route path="/app/orders" element={<div>Orders content</div>} />
            <Route path="/app" element={<div>App landing</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  )
}

describe('LoginRoute', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('redirects an unauthenticated deep link to /login', () => {
    renderTree('/app/orders')

    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument()
    expect(screen.queryByText('Orders content')).not.toBeInTheDocument()
  })

  it('returns to the originally-requested deep link after a successful sign-in', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ organizationId: 'org-1', userId: 'user-1', displayName: 'Jane Doe' }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )

    const user = userEvent.setup()
    renderTree('/app/orders')

    await user.type(screen.getByLabelText('Email'), 'jane@example.com')
    await user.type(screen.getByLabelText('Password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: /sign in/i }))

    await waitFor(() => expect(screen.getByText('Orders content')).toBeInTheDocument())
  })

  it('navigates to resolveLandingPath(user) on a direct /login visit with no "from" state', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ organizationId: 'org-1', userId: 'user-1', displayName: 'Jane Doe' }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )

    const user = userEvent.setup()
    renderTree('/login')

    await user.type(screen.getByLabelText('Email'), 'jane@example.com')
    await user.type(screen.getByLabelText('Password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: /sign in/i }))

    await waitFor(() => expect(screen.getByText('App landing')).toBeInTheDocument())
  })

  it('shows a Forgot password? link to /forgot-password', () => {
    renderTree('/login')

    expect(screen.getByRole('link', { name: /forgot password/i })).toHaveAttribute('href', '/forgot-password')
  })
})
