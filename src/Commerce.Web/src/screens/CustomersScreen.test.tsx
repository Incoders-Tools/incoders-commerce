import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomersScreen } from './CustomersScreen'
import type { CustomerRecord } from '@/api/types'

const listedCustomer: CustomerRecord = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  customerKind: 'Retail',
  displayName: 'Jane Doe',
  legalName: null,
  taxIdType: 'None',
  taxId: null,
  taxCondition: 'ConsumidorFinal',
  phone: '11-5555-5555',
  email: null,
  addressStreet: null,
  addressNumber: null,
  neighborhood: null,
  locality: null,
  province: null,
  postalCode: null,
  deliveryNotes: null,
  discountPercentage: null,
  paymentTerms: null,
  notes: null,
  isEnabled: true,
  createdAtUtc: '2024-01-01T00:00:00Z',
  createdByUserId: 'user-1',
  updatedAtUtc: '2024-01-01T00:00:00Z',
}

/**
 * design.md "Two admin UIs against one endpoint set" / Testing Strategy:
 * "CustomersScreen lists, creates, and edits against a mocked client."
 */
describe('CustomersScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('lists customers from GET /customers', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))

    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(fetchMock.mock.calls[0][0]).toBe('/customers')
  })

  it('shows an empty state when there are no customers', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }))

    render(<CustomersScreen />)

    await screen.findByText('No customers yet.')
  })

  it('opens the create form, saves, and refreshes the list', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 })) // initial list
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ customerId: '22222222-2222-2222-2222-222222222222' }), { status: 201 }),
      ) // create
      .mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 })) // refreshed list

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('No customers yet.')
    await user.click(screen.getByRole('button', { name: /new customer/i }))
    await user.type(screen.getByLabelText('Display name'), 'Jane Doe')
    await user.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(screen.getByText('Customers')).toBeInTheDocument())
    await screen.findByText('Jane Doe')
    expect(fetchMock).toHaveBeenCalledTimes(3)
  })

  it('opens the edit form for a listed customer', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /edit/i }))

    expect(screen.getByText('Edit customer')).toBeInTheDocument()
    expect(screen.getByLabelText('Customer kind')).toBeDisabled()
  })
})
