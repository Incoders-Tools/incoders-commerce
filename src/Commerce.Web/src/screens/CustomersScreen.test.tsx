import { render, screen, waitFor, within } from '@testing-library/react'
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

// T4b: a second, deliberately different record so the search/exclusion
// assertions below compare two rows the screen really renders, rather than
// asserting the absence of text nothing would ever produce.
const wholesaleCustomer: CustomerRecord = {
  ...listedCustomer,
  id: '99999999-9999-9999-9999-999999999999',
  customerKind: 'Wholesale',
  displayName: 'Acme Supplies',
  legalName: 'Acme Supplies SRL',
  taxIdType: 'Cuit',
  taxId: '30-12345678-9',
  isEnabled: false,
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
    // T4b: the view preference now persists between cases.
    window.localStorage.clear()
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
    await user.click(screen.getByRole('button', { name: /^edit$/i }))

    expect(screen.getByText('Edit customer')).toBeInTheDocument()
    expect(screen.getByLabelText('Customer kind')).toBeDisabled()
  })

  it('still issues ordering access and shows the one-time credential', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ credential: 'one-time-secret' }), { status: 200 }))

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /issue ordering access/i }))

    const credential = await screen.findByTestId('issued-credential')
    expect(credential).toHaveTextContent('one-time-secret')
    expect(fetchMock.mock.calls[1][0]).toBe(`/customers/${listedCustomer.id}/ordering-access`)
  })

  it('does not claim there are no customers when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<CustomersScreen />)

    await screen.findByRole('alert')
    // "No customers yet." is a real rendering of this screen (see the empty
    // state case above), so its absence here is a fact about this state.
    expect(screen.queryByText('No customers yet.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/customers could not be loaded/i)
  })

  it('does not blame the load when a failed action left an error on screen', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ title: 'Ordering access is already issued.' }), {
          status: 409,
          headers: { 'Content-Type': 'application/json' },
        }),
      )

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /issue ordering access/i }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/already issued/i)

    // The list loaded fine; a search with no matches must say so.
    await user.type(screen.getByLabelText(/search customers/i), 'zzzz')
    expect(screen.getByText('No customers match this search.')).toBeInTheDocument()
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
  })

  // ---- T4b: the shared data-view layer (PageHeader + DataToolbar + DataView) ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))

    const { container } = render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(container.querySelector('.max-w-3xl')).toBeNull()
    expect(container.querySelector('.mx-auto')).toBeNull()
  })

  it('renders the real customer columns for each listed record', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([wholesaleCustomer]), { status: 200 }))

    render(<CustomersScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Acme Supplies')).toBeInTheDocument()
    expect(within(row).getByText('Wholesale')).toBeInTheDocument()
    expect(within(row).getByText('Disabled')).toBeInTheDocument()
    expect(within(row).getByText('30-12345678-9')).toBeInTheDocument()
  })

  it('filters the listed customers client-side by name, legal name or tax id', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([listedCustomer, wholesaleCustomer]), { status: 200 }),
    )

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.getByText('Acme Supplies')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/search customers/i), 'acme')

    expect(screen.getByText('Acme Supplies')).toBeInTheDocument()
    expect(screen.queryByText('Jane Doe')).not.toBeInTheDocument()
    // One request only: the filter runs over what was already loaded.
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.clear(screen.getByLabelText(/search customers/i))
    await user.type(screen.getByLabelText(/search customers/i), '30-12345678-9')

    expect(screen.getByText('Acme Supplies')).toBeInTheDocument()
    expect(screen.queryByText('Jane Doe')).not.toBeInTheDocument()
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([listedCustomer, wholesaleCustomer]), { status: 200 }),
    )

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.type(screen.getByLabelText(/search customers/i), 'zzzz')

    expect(screen.getByText(/no customers match/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer, wholesaleCustomer]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer, wholesaleCustomer]), { status: 200 }))

    const user = userEvent.setup()
    const first = render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /card view/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:customers')).toBe('cards')

    first.unmount()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still edits a customer from the card view', async () => {
    window.localStorage.setItem('view:customers', 'cards')
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([listedCustomer]), { status: 200 }))

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    // Prove we really are in the card layout, not just re-testing the table.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)

    await user.click(screen.getByRole('button', { name: /^edit$/i }))

    expect(screen.getByText('Edit customer')).toBeInTheDocument()
  })
})
