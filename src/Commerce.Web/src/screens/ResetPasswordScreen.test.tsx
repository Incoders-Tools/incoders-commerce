import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ResetPasswordScreen } from './ResetPasswordScreen'

/**
 * Covers commerce-password-recovery task 4.10: the reset screen posts the
 * token it was given (read once from `?token=` by useResetToken()) plus the
 * new password, and calls onSuccess (which the App wires to clear() —
 * scrubbing the URL) only on success.
 */
describe('ResetPasswordScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('posts {token, newPassword} to /account/reset-password/confirm and calls onSuccess', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))
    const onSuccess = vi.fn()

    const user = userEvent.setup()
    render(<ResetPasswordScreen token="the-real-token" onSuccess={onSuccess} />)

    await user.type(screen.getByLabelText('Nueva contraseña'), 'brand-new-password')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/account/reset-password/confirm')
    expect(JSON.parse(init.body as string)).toEqual({ token: 'the-real-token', newPassword: 'brand-new-password' })

    await waitFor(() => expect(onSuccess).toHaveBeenCalledTimes(1))
  })

  it('shows a generic error on 401 without calling onSuccess', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }))
    const onSuccess = vi.fn()

    const user = userEvent.setup()
    render(<ResetPasswordScreen token="expired-token" onSuccess={onSuccess} />)

    await user.type(screen.getByLabelText('Nueva contraseña'), 'some-password')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(onSuccess).not.toHaveBeenCalled()
  })
})
