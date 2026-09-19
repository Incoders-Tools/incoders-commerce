import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ForgotPasswordScreen } from './ForgotPasswordScreen'

/**
 * Covers commerce-password-recovery task 4.10: the forgot-password screen
 * renders the SAME confirmation regardless of whether the submitted email
 * matched a real account or the request outright failed (spec: "Unknown
 * email looks identical to a known one").
 */
describe('ForgotPasswordScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('posts {email} to /account/reset-password/request and shows the uniform confirmation on success', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 202 }))

    const user = userEvent.setup()
    render(<ForgotPasswordScreen onBackToSignIn={() => {}} />)

    await user.type(screen.getByLabelText('Email'), 'known@example.com')
    await user.click(screen.getByRole('button', { name: /send reset link/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/account/reset-password/request')
    expect(JSON.parse(init.body as string)).toEqual({ email: 'known@example.com' })

    expect(await screen.findByRole('status')).toHaveTextContent(
      'If that address exists, check your inbox for a reset link.',
    )
  })

  it('shows the SAME confirmation even when the request fails outright', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('network error'))

    const user = userEvent.setup()
    render(<ForgotPasswordScreen onBackToSignIn={() => {}} />)

    await user.type(screen.getByLabelText('Email'), 'unreachable@example.com')
    await user.click(screen.getByRole('button', { name: /send reset link/i }))

    expect(await screen.findByRole('status')).toHaveTextContent(
      'If that address exists, check your inbox for a reset link.',
    )
  })
})
