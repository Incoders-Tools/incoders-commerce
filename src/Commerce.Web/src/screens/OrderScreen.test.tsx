import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrderScreen } from './OrderScreen'

/**
 * commerce-guest-ordering design.md "One screen, guest and registered as
 * peers" (ADR-009, locked): `OrderScreen` is ONE route with a peer
 * segmented control, guest listed first, no login-pressure copy anywhere,
 * and no self-registration form/link. tasks.md Phase 6, tasks 6.1-6.3/6.6.
 */
describe('OrderScreen', () => {
  const fetchMock = vi.fn()
  const presentation = {
    id: 'pres-1',
    organizationId: 'org-1',
    productId: 'prod-1',
    name: '1.5L bottle',
    quantityBehavior: 0,
    unitId: 'unit-1',
    identificationCode: null,
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
    updatedAtUtc: '2024-01-01T00:00:00Z',
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockImplementation((url: string) => {
      if (url === '/public/catalog/presentations') {
        return Promise.resolve(new Response(JSON.stringify([presentation]), { status: 200 }))
      }
      return Promise.reject(new Error(`Unexpected fetch: ${url}`))
    })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('renders both peer options with guest listed first and no login-pressure copy', async () => {
    render(<OrderScreen />)

    const guestOption = await screen.findByRole('tab', { name: /order as guest/i })
    const registeredOption = screen.getByRole('tab', { name: /sign in to order/i })

    const tabs = screen.getAllByRole('tab')
    expect(tabs[0]).toBe(guestOption)
    expect(tabs[1]).toBe(registeredOption)

    // No login-pressure copy anywhere on the screen.
    expect(screen.queryByText(/ya ten[eé]s cuenta/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/already have an account/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/recommended/i)).not.toBeInTheDocument()

    // No self-registration form/link exists anywhere on the screen.
    expect(screen.queryByText(/create an account/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/sign up/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/registrarse/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('defaults to the guest branch and shows no raw GUID input fields', async () => {
    render(<OrderScreen />)

    await screen.findByLabelText(/document/i)
    expect(screen.queryByLabelText(/customer id/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/access credential/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/destination branch id/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/actor id/i)).not.toBeInTheDocument()
  })

  it('runs the guest verification-then-confirm-then-submit sequence', async () => {
    fetchMock.mockImplementation((url: string, init?: RequestInit) => {
      if (url === '/public/catalog/presentations') {
        return Promise.resolve(new Response(JSON.stringify([presentation]), { status: 200 }))
      }
      if (url === '/public/guest-orders/verification' && init?.method === 'POST') {
        return Promise.resolve(
          new Response(JSON.stringify({ verificationId: 'verification-1' }), { status: 202 }),
        )
      }
      if (url === '/public/guest-orders/verification/confirm' && init?.method === 'POST') {
        return Promise.resolve(new Response(null, { status: 204 }))
      }
      if (url === '/public/guest-orders' && init?.method === 'POST') {
        return Promise.resolve(
          new Response(JSON.stringify({ status: 0, reason: 'allowed', order: { id: '1', organizationId: '1', status: 0, lines: [] } }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        )
      }
      return Promise.reject(new Error(`Unexpected fetch: ${url}`))
    })

    const user = userEvent.setup()
    render(<OrderScreen />)

    await user.type(await screen.findByLabelText(/document/i), '30111222'.slice(0, 8))
    await user.type(screen.getByLabelText(/^email/i), 'guest@example.com')
    await user.click(screen.getByRole('button', { name: /send verification code/i }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/public/guest-orders/verification',
        expect.objectContaining({ method: 'POST' }),
      ),
    )

    const codeInput = await screen.findByLabelText(/verification code/i)
    await user.type(codeInput, '123456')
    await user.click(screen.getByRole('button', { name: /confirm code/i }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/public/guest-orders/verification/confirm',
        expect.objectContaining({ method: 'POST' }),
      ),
    )

    await user.selectOptions(await screen.findByRole('combobox', { name: /presentation/i }), 'pres-1')
    await user.click(screen.getByRole('button', { name: /add line/i }))
    await user.click(screen.getByRole('button', { name: /submit order/i }))

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/public/guest-orders', expect.objectContaining({ method: 'POST' })),
    )

    const [, submitInit] = fetchMock.mock.calls.find(([url]) => url === '/public/guest-orders')!
    const body = JSON.parse(submitInit.body as string)
    expect(body.verificationId).toBe('verification-1')
    expect(body.documentId).toBe('30111222')
    expect(body.email).toBe('guest@example.com')
    expect(body.lines).toEqual([{ productId: 'prod-1', presentationId: 'pres-1', quantity: 1 }])

    await screen.findByTestId('order-outcome')
  })

  it('blocks an unverified submit attempt client-side with a clear message', async () => {
    const user = userEvent.setup()
    render(<OrderScreen />)

    await user.selectOptions(await screen.findByRole('combobox', { name: /presentation/i }), 'pres-1')
    await user.click(screen.getByRole('button', { name: /add line/i }))

    const submitButton = screen.getByRole('button', { name: /submit order/i })
    expect(submitButton).toBeDisabled()
    expect(screen.getByText(/confirm your verification code before submitting/i)).toBeInTheDocument()

    await user.click(submitButton)
    expect(fetchMock).not.toHaveBeenCalledWith('/public/guest-orders', expect.anything())
  })

  it('switches to the registered branch and shows a sign-in form with no raw GUID fields', async () => {
    const user = userEvent.setup()
    render(<OrderScreen />)

    await user.click(await screen.findByRole('tab', { name: /sign in to order/i }))

    const panel = screen.getByRole('tabpanel')
    expect(within(panel).getByLabelText(/^email/i)).toBeInTheDocument()
    expect(within(panel).getByLabelText(/password/i)).toBeInTheDocument()
    expect(within(panel).queryByLabelText(/customer id/i)).not.toBeInTheDocument()
    expect(within(panel).queryByLabelText(/access credential/i)).not.toBeInTheDocument()
  })
})
