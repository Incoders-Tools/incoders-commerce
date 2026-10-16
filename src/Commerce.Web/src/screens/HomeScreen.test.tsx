import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { HomeScreen } from './HomeScreen'

/**
 * `/` MUST render with zero dependency on auth/session machinery (web-app-
 * routing spec: "Public Routes Render Without Auth Dependency"). Mounting
 * with NO `AuthProvider` at all and asserting `fetch` is never called turns
 * that requirement into an executing assertion instead of a code-review
 * promise (design.md "`/` for a signed-in visitor").
 */
describe('HomeScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('renders without an AuthProvider and makes no auth/session request', () => {
    expect(() =>
      render(
        <MemoryRouter>
          <HomeScreen />
        </MemoryRouter>,
      ),
    ).not.toThrow()

    expect(screen.getByRole('heading', { name: 'Commerce' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /iniciar sesión/i })).toHaveAttribute('href', '/login')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('shows no app link when no auth context is present', () => {
    render(
      <MemoryRouter>
        <HomeScreen />
      </MemoryRouter>,
    )

    expect(screen.queryByRole('link', { name: /ir a la aplicación/i })).not.toBeInTheDocument()
  })

  it('shows a visible link into the app when a user is signed in', () => {
    render(
      <MemoryRouter>
        <AuthContext.Provider
          value={{
            user: { organizationId: 'org-1', userId: 'user-1', displayName: 'Jane Doe', permissions: 0, isSystemAdmin: false, selectableBranches: [] },
            error: null,
            signIn: vi.fn(),
            signOut: vi.fn(),
          }}
        >
          <HomeScreen />
        </AuthContext.Provider>
      </MemoryRouter>,
    )

    expect(screen.getByRole('heading', { name: 'Commerce' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /ir a la aplicación/i })).toHaveAttribute('href', '/app')
  })
})
