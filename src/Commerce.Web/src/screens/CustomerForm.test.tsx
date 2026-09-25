import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomerForm } from './CustomerForm'
import type { CustomerRecord } from '@/api/types'

const customer: CustomerRecord = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  customerKind: 'Wholesale',
  displayName: 'Existing Co.',
  legalName: 'Existing Co. SRL',
  taxIdType: 'Cuit',
  taxId: '20-12345678-9',
  taxCondition: 'ResponsableInscripto',
  phone: '11-5555-5555',
  email: 'existing@example.com',
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
 * design.md "Web form shape (create vs. edit)": one component, two modes.
 * `CustomerKind` is a required select at create and READ-ONLY at edit.
 */
describe('CustomerForm', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('creates a Retail customer with only displayName filled, POSTing the real shape', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ customerId: '22222222-2222-2222-2222-222222222222' }), {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const onSaved = vi.fn()
    const user = userEvent.setup()
    render(<CustomerForm onSaved={onSaved} onCancel={vi.fn()} />)

    await user.type(screen.getByLabelText('Display name'), 'Jane Doe')
    await user.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/customers')
    expect(init.method).toBe('POST')

    const body = JSON.parse(init.body as string)
    expect(body.customerKind).toBe('Retail')
    expect(body.displayName).toBe('Jane Doe')
    expect(body.taxId).toBeNull()

    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1))
  })

  it('disables CustomerKind in edit mode and PUTs without it', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(customer), { status: 200 }))

    const onSaved = vi.fn()
    const user = userEvent.setup()
    render(<CustomerForm customer={customer} onSaved={onSaved} onCancel={vi.fn()} />)

    expect(screen.getByLabelText('Customer kind')).toBeDisabled()

    await user.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe(`/customers/${customer.id}`)
    expect(init.method).toBe('PUT')

    const body = JSON.parse(init.body as string)
    expect(body).not.toHaveProperty('customerKind')
    expect(body.isEnabled).toBe(true)

    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1))
  })

  it('renders on the full-screen FormPage shell, with a back action that also cancels', async () => {
    const onCancel = vi.fn()
    const user = userEvent.setup()
    render(<CustomerForm onSaved={vi.fn()} onCancel={onCancel} />)

    expect(screen.getByRole('heading', { name: 'New customer' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /back to customers/i }))
    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('uses the full width the shell gives it, with no centered narrow column', () => {
    const { container } = render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    expect(container.querySelector('[class*="max-w-lg"]')).toBeNull()
  })

  it('renders every select with semantic design tokens, dark-mode-safe', () => {
    render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    const selects = [
      screen.getByLabelText('Customer kind'),
      screen.getByLabelText('Tax ID type'),
      screen.getByLabelText('Tax condition'),
    ]
    for (const select of selects) {
      expect(select.className).not.toMatch(/border-neutral-300|bg-white/)
    }
  })
})
