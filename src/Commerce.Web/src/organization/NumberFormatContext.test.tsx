import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import type { SignedInResponse } from '@/api/types'
import { NumberFormatProvider, useNumberFormat } from './NumberFormatContext'

vi.mock('@/api/account', () => ({ getOwnOrganizationSettings: vi.fn() }))
import { getOwnOrganizationSettings } from '@/api/account'

const getSettings = vi.mocked(getOwnOrganizationSettings)

function Probe() {
  const format = useNumberFormat()
  return (
    <div>
      <span data-testid="separator">{format.separator}</span>
      <span data-testid="parsed">{String(format.parse('1.5'))}</span>
      <span data-testid="formatted">{format.formatStock(117.5, 'Weighted')}</span>
      <span data-testid="example">{format.example}</span>
      <span data-testid="error">{format.errorFor('1,5') ?? 'none'}</span>
    </div>
  )
}

const user: SignedInResponse = {
  organizationId: 'org-1',
  userId: 'user-1',
  displayName: 'Ada',
  permissions: 0,
  isSystemAdmin: false,
  selectableBranches: [],
}

function renderAs(signedIn: SignedInResponse | null) {
  return render(
    <AuthContext.Provider value={{ user: signedIn, error: null, signIn: async () => {}, signOut: async () => {} }}>
      <NumberFormatProvider>
        <Probe />
      </NumberFormatProvider>
    </AuthContext.Provider>,
  )
}

describe('NumberFormatProvider', () => {
  afterEach(() => {
    cleanup()
    getSettings.mockReset()
  })

  it('falls back to the comma format outside any provider', () => {
    render(<Probe />)
    expect(screen.getByTestId('separator')).toHaveTextContent('Comma')
    expect(screen.getByTestId('parsed')).toHaveTextContent('null')
    expect(screen.getByTestId('formatted')).toHaveTextContent('117,5 kg')
  })

  it('follows the organization separator: Dot parses and shows decimals with a point', async () => {
    getSettings.mockResolvedValue({ quantityDecimalSeparator: 'Dot' })
    renderAs(user)
    await waitFor(() => expect(screen.getByTestId('separator')).toHaveTextContent('Dot'))
    expect(screen.getByTestId('parsed')).toHaveTextContent('1.5')
    expect(screen.getByTestId('formatted')).toHaveTextContent('117.5 kg')
    expect(screen.getByTestId('example')).toHaveTextContent('Ej: 1.5')
    expect(screen.getByTestId('error')).toHaveTextContent('En este negocio los decimales se escriben con punto')
  })

  it('stays on Comma when the settings cannot be loaded, and when nobody is signed in', async () => {
    getSettings.mockRejectedValue(new Error('boom'))
    renderAs(user)
    await waitFor(() => expect(getSettings).toHaveBeenCalled())
    expect(screen.getByTestId('separator')).toHaveTextContent('Comma')
    cleanup()
    getSettings.mockClear()
    renderAs(null)
    expect(getSettings).not.toHaveBeenCalled()
    expect(screen.getByTestId('separator')).toHaveTextContent('Comma')
  })
})
