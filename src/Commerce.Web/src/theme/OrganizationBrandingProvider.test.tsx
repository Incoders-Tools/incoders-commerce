import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import type { SignedInResponse } from '@/api/types'
import { OrganizationBrandingProvider, useOrganizationBranding } from './OrganizationBrandingProvider'

vi.mock('@/api/account', () => ({
  getOwnOrganizationBranding: vi.fn(),
}))

import { getOwnOrganizationBranding } from '@/api/account'

const getOwnOrganizationBrandingMock = vi.mocked(getOwnOrganizationBranding)

function TestConsumer() {
  const { branding, loading } = useOrganizationBranding()
  return (
    <div>
      <span data-testid="loading">{String(loading)}</span>
      <span data-testid="logoUrl">{branding?.logoUrl ?? 'none'}</span>
      <span data-testid="primaryColor">{branding?.primaryColor ?? 'none'}</span>
    </div>
  )
}

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

function renderAsUser(user: SignedInResponse | null) {
  return render(
    <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
      <OrganizationBrandingProvider>
        <TestConsumer />
      </OrganizationBrandingProvider>
    </AuthContext.Provider>,
  )
}

describe('OrganizationBrandingProvider', () => {
  afterEach(() => {
    cleanup()
    getOwnOrganizationBrandingMock.mockReset()
  })

  it('fetches the signed-in user\'s own organization branding once', async () => {
    getOwnOrganizationBrandingMock.mockResolvedValueOnce({
      logoUrl: 'https://cdn.example.com/logo.png',
      primaryColor: '#336699',
    })

    renderAsUser(buildUser())

    await waitFor(() => expect(screen.getByTestId('logoUrl')).toHaveTextContent('https://cdn.example.com/logo.png'))
    expect(screen.getByTestId('primaryColor')).toHaveTextContent('#336699')
    expect(getOwnOrganizationBrandingMock).toHaveBeenCalledTimes(1)
  })

  it('does not fetch and stays null when no user is signed in', async () => {
    renderAsUser(null)

    expect(screen.getByTestId('loading')).toHaveTextContent('false')
    expect(screen.getByTestId('logoUrl')).toHaveTextContent('none')
    expect(getOwnOrganizationBrandingMock).not.toHaveBeenCalled()
  })

  it('swallows a failed fetch (system admin with no org scope, 403/404, network) and stays null', async () => {
    getOwnOrganizationBrandingMock.mockRejectedValueOnce(new Error('403'))

    renderAsUser(buildUser({ isSystemAdmin: true }))

    await waitFor(() => expect(screen.getByTestId('loading')).toHaveTextContent('false'))
    expect(screen.getByTestId('logoUrl')).toHaveTextContent('none')
    expect(screen.getByTestId('primaryColor')).toHaveTextContent('none')
  })

  it('refetches when the signed-in identity changes (sign-in as someone else)', async () => {
    getOwnOrganizationBrandingMock.mockResolvedValueOnce({ logoUrl: null, primaryColor: '#111111' })

    const { rerender } = render(
      <AuthContext.Provider
        value={{ user: buildUser({ userId: 'user-1' }), error: null, signIn: async () => {}, signOut: async () => {} }}
      >
        <OrganizationBrandingProvider>
          <TestConsumer />
        </OrganizationBrandingProvider>
      </AuthContext.Provider>,
    )

    await waitFor(() => expect(screen.getByTestId('primaryColor')).toHaveTextContent('#111111'))

    getOwnOrganizationBrandingMock.mockResolvedValueOnce({ logoUrl: null, primaryColor: '#222222' })

    rerender(
      <AuthContext.Provider
        value={{
          user: buildUser({ userId: 'user-2', organizationId: 'org-2' }),
          error: null,
          signIn: async () => {},
          signOut: async () => {},
        }}
      >
        <OrganizationBrandingProvider>
          <TestConsumer />
        </OrganizationBrandingProvider>
      </AuthContext.Provider>,
    )

    await waitFor(() => expect(screen.getByTestId('primaryColor')).toHaveTextContent('#222222'))
    expect(getOwnOrganizationBrandingMock).toHaveBeenCalledTimes(2)
  })

  it('clears branding on sign-out', async () => {
    getOwnOrganizationBrandingMock.mockResolvedValueOnce({ logoUrl: null, primaryColor: '#111111' })

    const { rerender } = render(
      <AuthContext.Provider
        value={{ user: buildUser(), error: null, signIn: async () => {}, signOut: async () => {} }}
      >
        <OrganizationBrandingProvider>
          <TestConsumer />
        </OrganizationBrandingProvider>
      </AuthContext.Provider>,
    )

    await waitFor(() => expect(screen.getByTestId('primaryColor')).toHaveTextContent('#111111'))

    rerender(
      <AuthContext.Provider value={{ user: null, error: null, signIn: async () => {}, signOut: async () => {} }}>
        <OrganizationBrandingProvider>
          <TestConsumer />
        </OrganizationBrandingProvider>
      </AuthContext.Provider>,
    )

    expect(screen.getByTestId('primaryColor')).toHaveTextContent('none')
  })
})
