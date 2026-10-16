import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/auth/AuthContext'
import { SignInScreen } from './SignInScreen'

/**
 * Proves the sign-in screen posts the real `{email, password}` credential
 * shape (Endpoints/Account.cs `POST /account/sign-in`) rather than the prior
 * client-trusted org/user uuid + display name shape — the shape this change
 * closes (commerce-user-credentials design.md "Interfaces / Contracts").
 */
describe('SignInScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('POSTs {email, password} to /account/sign-in with no org/user/displayName fields', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ organizationId: '1', userId: '2', displayName: 'jane@example.com' }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )

    const user = userEvent.setup()
    render(
      <AuthProvider>
        <SignInScreen />
      </AuthProvider>,
    )

    await user.type(screen.getByLabelText('Correo electrónico'), 'jane@example.com')
    await user.type(screen.getByLabelText('Contraseña'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: /iniciar sesión/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/account/sign-in')
    expect(init.method).toBe('POST')

    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ email: 'jane@example.com', password: 'correct-horse-battery-staple' })
    expect(body).not.toHaveProperty('organizationId')
    expect(body).not.toHaveProperty('userId')
    expect(body).not.toHaveProperty('displayName')
  })

  it('surfaces a visible error on 401 without leaking whether the email or password was wrong', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }))

    const user = userEvent.setup()
    render(
      <AuthProvider>
        <SignInScreen />
      </AuthProvider>,
    )

    await user.type(screen.getByLabelText('Correo electrónico'), 'jane@example.com')
    await user.type(screen.getByLabelText('Contraseña'), 'wrong-password')
    await user.click(screen.getByRole('button', { name: /iniciar sesión/i }))

    const alert = await screen.findByRole('alert')
    expect(alert).toBeInTheDocument()
  })
})
