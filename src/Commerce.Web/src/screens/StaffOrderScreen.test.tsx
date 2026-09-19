import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { StaffOrderScreen } from './StaffOrderScreen'

/**
 * tasks.md 6.8 regression guard: the pre-existing staff-operated submission
 * path (`POST /orders/` via `submitOrder`) is UNCHANGED behaviorally after
 * `OrderScreen.tsx` was reworked into the public guest/registered peer
 * screen — this is that same coverage, extracted verbatim from the former
 * `OrderScreen.test.tsx` onto the extracted `StaffOrderScreen`.
 */
describe('StaffOrderScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('Customer ID'), 'c1')
    await user.type(screen.getByLabelText('Access credential'), 'cred1')
    await user.type(screen.getByLabelText('Destination branch ID'), 'branch1')
    await user.type(screen.getByLabelText('Actor ID'), 'actor1')
    await user.type(screen.getByLabelText('Product ID'), 'prod1')
    await user.type(screen.getByLabelText('Presentation ID'), 'pres1')
    await user.click(screen.getByRole('button', { name: /submit order/i }))
  }

  it('POSTs to the real /orders/ route with the exact request shape', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({ status: 0, reason: 'allowed', order: { id: '1', organizationId: '1', status: 0 } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )

    const user = userEvent.setup()
    render(<StaffOrderScreen />)
    await fillAndSubmit(user)

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/orders/')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('include')

    const body = JSON.parse(init.body as string)
    expect(body.customerId).toBe('c1')
    expect(body.destinationBranchId).toBe('branch1')
    expect(body.accessEnabled).toBeUndefined()
    expect(body.lines).toHaveLength(1)
    expect(body.lines[0]).toMatchObject({
      productId: 'prod1',
      presentationId: 'pres1',
      quantity: 1,
    })
    expect(body.lines[0].productName).toBeUndefined()
    expect(body.lines[0].presentationName).toBeUndefined()
    expect(body.lines[0].unitId).toBeUndefined()

    await screen.findByTestId('order-outcome')
    expect(screen.getByTestId('order-outcome')).toHaveTextContent('Order accepted.')
  })

  it('surfaces a visible error state when the API is unreachable', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const user = userEvent.setup()
    render(<StaffOrderScreen />)
    await fillAndSubmit(user)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/unreachable/i)
  })
})
