import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomersScreen } from './CustomersScreen'
import type { CustomerRecord, MasterDataEntry } from '@/api/types'

const listedCustomer: CustomerRecord = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  customerKind: 'Retail',
  displayName: 'Jane Doe',
  legalName: null,
  contacts: [
    { id: 'ct-0', firstName: 'Ana', lastName: 'Gómez', phone: null, email: null, role: null, isPrimary: false, sortOrder: 0 },
    { id: 'ct-1', firstName: 'Juana', lastName: 'Pérez', phone: null, email: null, role: null, isPrimary: true, sortOrder: 1 },
  ],
  cityId: null,
  cityName: null,
  provinceId: null,
  provinceName: null,
  businessTypeId: null,
  businessTypeName: null,
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

// A second, deliberately different record so the exclusion assertions compare
// two rows the screen really renders.
const wholesaleCustomer: CustomerRecord = {
  ...listedCustomer,
  id: '99999999-9999-9999-9999-999999999999',
  customerKind: 'Wholesale',
  displayName: 'Acme Supplies',
  legalName: 'Acme Supplies SRL',
  contacts: [
    { id: 'ct-2', firstName: 'Roberto', lastName: null, phone: null, email: null, role: null, isPrimary: true, sortOrder: 0 },
  ],
  cityId: 'city-rosario',
  cityName: 'Rosario',
  provinceId: '82',
  provinceName: 'Santa Fe',
  businessTypeId: 'bt-bar',
  businessTypeName: 'Bar',
  taxIdType: 'Cuit',
  taxId: '30-12345678-9',
  isEnabled: false,
}

const entry = (id: string, name: string, isActive = true): MasterDataEntry => ({
  id,
  organizationId: 'org-1',
  name,
  key: name.toLowerCase(),
  sortOrder: 1,
  isActive,
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
})

const geoCity = (id: string, name: string, provinceName: string) => ({
  id,
  indecId: null,
  name,
  provinceId: '82',
  provinceName,
  countryCode: 'AR',
  departmentName: null,
  isActive: true,
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
})
const cities = [geoCity('city-rosario', 'Rosario', 'Santa Fe'), geoCity('city-funes', 'Funes', 'Santa Fe')]
const businessTypes = [entry('bt-bar', 'Bar'), entry('bt-resto', 'Restaurante')]

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

/**
 * design.md "Two admin UIs against one endpoint set" / Testing Strategy:
 * "CustomersScreen lists, creates, and edits against a mocked client." The
 * mock answers by URL because the screen also loads the two catalogs.
 */
describe('CustomersScreen', () => {
  const fetchMock = vi.fn()
  let customers: CustomerRecord[]

  /** URLs (with query) requested from `/customers` itself, in order. */
  const customerListCalls = () =>
    fetchMock.mock.calls
      .filter((call) => call[1]?.method === undefined)
      .map((call) => call[0] as string)
      .filter((url) => url === '/customers' || url.startsWith('/customers?'))

  beforeEach(() => {
    customers = [listedCustomer]
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/geo/cities')) return json(cities)
      if (url.startsWith('/customers/business-types')) return json(businessTypes)
      if (url.endsWith('/ordering-access')) return json({ credential: 'one-time-secret' })
      if (init?.method === 'POST') return json({ customerId: '22222222-2222-2222-2222-222222222222' }, 201)
      return json(customers)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists customers from GET /customers', async () => {
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(customerListCalls()[0]).toBe('/customers')
  })

  it('shows an empty state when there are no customers', async () => {
    customers = []
    render(<CustomersScreen />)

    await screen.findByText('Todavía no hay clientes.')
  })

  it('opens the create form, saves, and refreshes the list', async () => {
    customers = []
    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Todavía no hay clientes.')
    await user.click(screen.getByRole('button', { name: /nuevo cliente/i }))
    await user.type(screen.getByLabelText('Nombre'), 'Jane Doe')
    customers = [listedCustomer]
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(screen.getByText('Clientes')).toBeInTheDocument())
    await screen.findByText('Jane Doe')
    expect(customerListCalls()).toHaveLength(2)
  })

  it('opens the edit form for a listed customer', async () => {
    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /^editar$/i }))

    expect(screen.getByText('Editar cliente')).toBeInTheDocument()
    expect(screen.getByLabelText('Tipo de cliente')).toBeDisabled()
  })

  it('still issues ordering access and shows the one-time credential', async () => {
    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /emitir acceso para pedidos/i }))

    const credential = await screen.findByTestId('issued-credential')
    expect(credential).toHaveTextContent('one-time-secret')
    expect(fetchMock.mock.calls.map((call) => call[0])).toContain(`/customers/${listedCustomer.id}/ordering-access`)
  })

  it('does not claim there are no customers when the load failed', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url === '/customers') throw new TypeError('Failed to fetch')
      return json([])
    })

    render(<CustomersScreen />)

    await screen.findByRole('alert')
    expect(screen.queryByText('Todavía no hay clientes.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/no se pudieron cargar los clientes/i)
  })

  it('does not blame the load when a failed action left an error on screen', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url.startsWith('/geo/cities') || url.startsWith('/customers/business-types')) return json([])
      if (url.endsWith('/ordering-access')) {
        return json({ title: 'Ordering access is already issued.' }, 409)
      }
      return json(customers)
    })

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /emitir acceso para pedidos/i }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/already issued/i)

    customers = []
    await user.type(screen.getByLabelText(/buscar clientes/i), 'zzzz')
    expect(await screen.findByText('Ningún cliente coincide con esta búsqueda.')).toBeInTheDocument()
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
  })

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    const { container } = render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(container.querySelector('.max-w-3xl')).toBeNull()
    expect(container.querySelector('.mx-auto')).toBeNull()
  })

  it('renders the real customer columns for each listed record', async () => {
    customers = [wholesaleCustomer]
    render(<CustomersScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Acme Supplies')).toBeInTheDocument()
    expect(within(row).getByText('Roberto')).toBeInTheDocument()
    expect(within(row).getByText('Rosario — Santa Fe')).toBeInTheDocument()
    expect(within(row).getByText('Bar')).toBeInTheDocument()
    expect(within(row).getByText('Mayorista')).toBeInTheDocument()
    expect(within(row).getByText('Deshabilitado')).toBeInTheDocument()
    expect(within(row).getByText('30-12345678-9')).toBeInTheDocument()
  })

  it('shows the primary contact (first and last name) in the contact column', async () => {
    customers = [listedCustomer]
    render(<CustomersScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Juana Pérez')).toBeInTheDocument()
    expect(within(row).queryByText('Ana Gómez')).not.toBeInTheDocument()
  })

  it('shows a placeholder when the customer has no contacts', async () => {
    customers = [{ ...listedCustomer, contacts: [] }]
    render(<CustomersScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getAllByText('—').length).toBeGreaterThan(0)
    expect(within(row).queryByText(/Juana/)).not.toBeInTheDocument()
  })

  it('reloads the edited customer after a modification conflict and shows the fresh data', async () => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/customers/business-types')) return json(businessTypes)
      if (init?.method === 'PUT') return json({ error: 'customer-modified' }, 409)
      if (url === `/customers/${listedCustomer.id}`) {
        return json({ ...listedCustomer, displayName: 'Jane Reloaded', updatedAtUtc: '2024-03-03T00:00:00Z' })
      }
      return json(customers)
    })
    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    await user.click(screen.getByRole('button', { name: /^editar$/i }))
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))
    await user.click(await screen.findByRole('button', { name: 'Recargar' }))

    await waitFor(() => expect(document.getElementById('displayName')).toHaveValue('Jane Reloaded'))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('searches on the server, debounced, instead of filtering client-side', async () => {
    const user = userEvent.setup()
    render(<CustomersScreen />)
    await screen.findByText('Jane Doe')

    customers = [wholesaleCustomer]
    await user.type(screen.getByLabelText(/buscar clientes/i), 'acme')

    await screen.findByText('Acme Supplies')
    expect(screen.queryByText('Jane Doe')).not.toBeInTheDocument()
    const calls = customerListCalls()
    // Typing four characters must not issue one request per keystroke.
    expect(calls).toHaveLength(2)
    expect(calls[1]).toBe('/customers?search=acme')
  })

  it('filters by city and by business type on the server', async () => {
    const user = userEvent.setup()
    render(<CustomersScreen />)
    await screen.findByText('Jane Doe')

    customers = [wholesaleCustomer]
    await user.click(screen.getByRole('combobox', { name: 'Ciudad' }))
    await user.click(await screen.findByRole('option', { name: 'Rosario — Santa Fe' }))
    await screen.findByText('Acme Supplies')
    expect(customerListCalls().at(-1)).toBe('/customers?cityId=city-rosario')

    await user.selectOptions(screen.getByLabelText('Tipo de negocio'), 'bt-bar')
    await waitFor(() =>
      expect(customerListCalls().at(-1)).toBe('/customers?cityId=city-rosario&businessTypeId=bt-bar'),
    )
  })

  it('does not load the whole city catalog to offer the city filter', async () => {
    render(<CustomersScreen />)
    await screen.findByText('Jane Doe')

    expect(fetchMock.mock.calls.some((call) => (call[0] as string).startsWith('/geo/cities'))).toBe(false)
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    const user = userEvent.setup()
    render(<CustomersScreen />)
    await screen.findByText('Jane Doe')

    customers = []
    await user.type(screen.getByLabelText(/buscar clientes/i), 'zzzz')

    expect(await screen.findByText(/ningún cliente coincide/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    customers = [listedCustomer, wholesaleCustomer]

    const user = userEvent.setup()
    const first = render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:customers')).toBe('cards')

    first.unmount()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still edits a customer from the card view', async () => {
    window.localStorage.setItem('view:customers', 'cards')

    const user = userEvent.setup()
    render(<CustomersScreen />)

    await screen.findByText('Jane Doe')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)

    await user.click(screen.getByRole('button', { name: /^editar$/i }))

    expect(screen.getByText('Editar cliente')).toBeInTheDocument()
  })
})
