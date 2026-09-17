import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RenewPasswordScreen } from './RenewPasswordScreen'

/**
 * Covers commerce-password-recovery task 4.10: the renew screen posts BOTH
 * the current and the new password to /account/renew-password.
 */
describe('RenewPasswordScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('posts {currentPassword, newPassword} to /account/renew-password and shows a success message', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<RenewPasswordScreen />)

    await user.type(screen.getByLabelText('Current password'), 'old-password')
    await user.type(screen.getByLabelText('New password'), 'new-password')
    await user.click(screen.getByRole('button', { name: /change password/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/account/renew-password')
    expect(JSON.parse(init.body as string)).toEqual({ currentPassword: 'old-password', newPassword: 'new-password' })

    expect(await screen.findByRole('status')).toHaveTextContent('Your password has been changed.')
  })

  it('shows an error on 401 without a success message', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }))

    const user = userEvent.setup()
    render(<RenewPasswordScreen />)

    await user.type(screen.getByLabelText('Current password'), 'wrong-password')
    await user.type(screen.getByLabelText('New password'), 'new-password')
    await user.click(screen.getByRole('button', { name: /change password/i }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })
})
