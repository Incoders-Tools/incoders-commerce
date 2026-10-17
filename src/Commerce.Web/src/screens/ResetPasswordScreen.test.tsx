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
    await user.type(screen.getByLabelText('Confirmar contraseña'), 'brand-new-password')
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
    await user.type(screen.getByLabelText('Confirmar contraseña'), 'some-password')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Este enlace de restablecimiento no es válido o venció.')
    expect(onSuccess).not.toHaveBeenCalled()
  })

  // railway-deploy-pipeline S15: the new password is asked twice; when they differ nothing is sent.
  it('sends nothing and says so when the confirmation differs', async () => {
    const onSuccess = vi.fn()

    const user = userEvent.setup()
    render(<ResetPasswordScreen token="the-real-token" onSuccess={onSuccess} />)

    await user.type(screen.getByLabelText('Nueva contraseña'), 'brand-new-password')
    await user.type(screen.getByLabelText('Confirmar contraseña'), 'brand-new-passwort')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    expect((await screen.findByRole('alert')).textContent).toBe('Las contraseñas no coinciden.')
    expect(fetchMock).not.toHaveBeenCalled()
    expect(onSuccess).not.toHaveBeenCalled()
  })

  // railway-deploy-pipeline S16: the 12-character minimum is said before sending, and 11 characters are not sent.
  it('says the 12-character minimum and sends nothing for 11 characters', async () => {
    const user = userEvent.setup()
    render(<ResetPasswordScreen token="the-real-token" onSuccess={vi.fn()} />)

    expect(screen.getByLabelText('Nueva contraseña')).toHaveAccessibleDescription('Mínimo 12 caracteres.')
    await user.type(screen.getByLabelText('Nueva contraseña'), 'eleven-char')
    await user.type(screen.getByLabelText('Confirmar contraseña'), 'eleven-char')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('La contraseña debe tener al menos 12 caracteres.')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('reads the server refusing the password as the minimum, not as an invalid link', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ errors: { newPassword: ['newPassword must be at least 12 characters.'] } }), { status: 400 }),
    )
    const onSuccess = vi.fn()

    const user = userEvent.setup()
    render(<ResetPasswordScreen token="the-real-token" onSuccess={onSuccess} />)

    await user.type(screen.getByLabelText('Nueva contraseña'), 'twelve-chars')
    await user.type(screen.getByLabelText('Confirmar contraseña'), 'twelve-chars')
    await user.click(screen.getByRole('button', { name: /restablecer contraseña/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('La contraseña debe tener al menos 12 caracteres.')
    expect(onSuccess).not.toHaveBeenCalled()
  })
})
