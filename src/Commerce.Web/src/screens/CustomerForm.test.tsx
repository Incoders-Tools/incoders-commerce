import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomerForm } from './CustomerForm'
import type { CustomerRecord, MasterDataEntry } from '@/api/types'

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
const NO_ID = '00000000-0000-0000-0000-000000000000'

const customer: CustomerRecord = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  customerKind: 'Wholesale',
  displayName: 'Existing Co.',
  legalName: 'Existing Co. SRL',
  taxIdType: 'Cuit',
  taxId: '20-12345678-9',
  taxCondition: 'ResponsableInscripto',
  contacts: [],
  cityId: null,
  cityName: null,
  provinceId: null,
  provinceName: null,
  businessTypeId: null,
  businessTypeName: null,
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
    fetchMock.mockImplementation(async (url: string) => {
      if (url.startsWith('/geo/cities')) {
        return new Response(
          JSON.stringify(
            [
              ['city-1', 'Rosario'],
              ['city-2', 'Funes'],
            ].map(([id, name]) => ({ id, name, provinceId: '82', provinceName: 'Santa Fe', isActive: true })),
          ),
          { status: 200 },
        )
      }
      return new Response(JSON.stringify({ customerId: 'x' }), { status: 201 })
    })
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

    await user.type(screen.getByLabelText('Nombre'), 'Jane Doe')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

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

    expect(screen.getByLabelText('Tipo de cliente')).toBeDisabled()

    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

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

    expect(screen.getByRole('heading', { name: 'Nuevo cliente' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /volver a clientes/i }))
    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('uses the full width the shell gives it, with no centered narrow column', () => {
    const { container } = render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    expect(container.querySelector('[class*="max-w-lg"]')).toBeNull()
  })

  it('renders every select with semantic design tokens, dark-mode-safe', () => {
    render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    const selects = [
      screen.getByLabelText('Tipo de cliente'),
      screen.getByLabelText('Tipo de identificación fiscal'),
      screen.getByLabelText('Condición fiscal'),
    ]
    for (const select of selects) {
      expect(select.className).not.toMatch(/border-neutral-300|bg-white/)
    }
  })

  it('groups the fields into the Datos, Contacto, Ubicación, Fiscal, Comercial and Observaciones sections', () => {
    render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    for (const name of ['Datos', 'Contacto', 'Ubicación', 'Fiscal', 'Comercial', 'Observaciones']) {
      expect(screen.getByRole('group', { name })).toBeInTheDocument()
    }
    expect(screen.getByLabelText('Observaciones')).toBeInTheDocument()
  })

  it('sends a city found with the picker and a business type chosen from the catalog', async () => {
    const user = userEvent.setup()
    render(
      <CustomerForm
        businessTypes={[entry('bt-1', 'Bar')]}
        onSaved={vi.fn()}
        onCancel={vi.fn()}
      />,
    )

    await user.type(screen.getByLabelText('Nombre'), 'Jane Doe')
    await user.click(screen.getByRole('combobox', { name: 'Ciudad' }))
    await user.click(await screen.findByRole('option', { name: 'Funes — Santa Fe' }))
    await user.selectOptions(screen.getByLabelText('Tipo de negocio'), 'bt-1')
    await user.type(screen.getByLabelText('Observaciones'), 'Paga los viernes')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'POST')).toBe(true))
    const post = fetchMock.mock.calls.find((call) => call[1]?.method === 'POST')!
    const body = JSON.parse(post[1].body as string)
    expect(body).not.toHaveProperty('contactName')
    expect(body).toMatchObject({
      cityId: 'city-2',
      businessTypeId: 'bt-1',
      notes: 'Paga los viernes',
    })
  })

  it('shows the customer current city and province without fetching the catalog', () => {
    render(
      <CustomerForm
        customer={{ ...customer, cityId: 'city-old', cityName: 'Zárate', provinceId: '06', provinceName: 'Buenos Aires' }}
        onSaved={vi.fn()}
        onCancel={vi.fn()}
      />,
    )

    expect(screen.getByRole('combobox', { name: 'Ciudad' })).toHaveValue('Zárate — Buenos Aires')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('offers only active catalog business types, plus the customer current one even if inactive', () => {
    render(
      <CustomerForm
        customer={{ ...customer, businessTypeId: 'bt-old', businessTypeName: 'Kiosco' }}
        businessTypes={[entry('bt-1', 'Bar'), entry('bt-old', 'Kiosco', false), entry('bt-x', 'Resto', false)]}
        onSaved={vi.fn()}
        onCancel={vi.fn()}
      />,
    )

    const type = screen.getByLabelText('Tipo de negocio')
    expect(within(type).getByRole('option', { name: 'Bar' })).toBeInTheDocument()
    expect(within(type).getByRole('option', { name: /Kiosco/ })).toBeInTheDocument()
    expect(within(type).queryByRole('option', { name: /Resto/ })).not.toBeInTheDocument()
    expect(type).toHaveValue('bt-old')
  })

  it('clears the city and business type on edit with the empty-id sentinel', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(customer), { status: 200 }))
    const user = userEvent.setup()
    render(
      <CustomerForm
        customer={{
          ...customer,
          cityId: 'city-1',
          cityName: 'Rosario',
          provinceName: 'Santa Fe',
          businessTypeId: 'bt-1',
        }}
        businessTypes={[entry('bt-1', 'Bar')]}
        onSaved={vi.fn()}
        onCancel={vi.fn()}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'Quitar ciudad' }))
    await user.selectOptions(screen.getByLabelText('Tipo de negocio'), '')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    const body = JSON.parse(fetchMock.mock.calls[0][1].body as string)
    expect(body).toMatchObject({ cityId: NO_ID, businessTypeId: NO_ID })
  })

  it('accepts a DNI of 7 or 8 digits and rejects anything else before calling the API', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ customerId: 'x' }), { status: 201 }))
    const user = userEvent.setup()
    render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    await user.type(screen.getByLabelText('Nombre'), 'Jane Doe')
    await user.selectOptions(screen.getByLabelText('Tipo de identificación fiscal'), 'Dni')
    await user.type(screen.getByLabelText('DNI'), '123456')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    expect(await screen.findByText('El DNI debe tener 7 u 8 dígitos.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()

    await user.clear(screen.getByLabelText('DNI'))
    await user.type(screen.getByLabelText('DNI'), '12.345.678')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toMatchObject({ taxIdType: 'Dni' })
  })

  it('requires 11 digits for a CUIT, ignoring separators', async () => {
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ customerId: 'x' }), { status: 201 }))
    const user = userEvent.setup()
    render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

    await user.type(screen.getByLabelText('Nombre'), 'Acme')
    await user.selectOptions(screen.getByLabelText('Tipo de identificación fiscal'), 'Cuit')
    await user.type(screen.getByLabelText('CUIT/CUIL'), '30-1234567-9')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    expect(await screen.findByText('El CUIT/CUIL debe tener 11 dígitos.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()

    await user.clear(screen.getByLabelText('CUIT/CUIL'))
    await user.type(screen.getByLabelText('CUIT/CUIL'), '30-12345678-9')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))
  })

  describe('contacts editor', () => {
    const juana = {
      id: 'ct-1',
      firstName: 'Juana',
      lastName: 'Pérez',
      phone: '341-555',
      email: 'juana@example.com',
      role: 'Compras',
      isPrimary: true,
      sortOrder: 0,
    }
    const roberto = { ...juana, id: 'ct-2', firstName: 'Roberto', lastName: null, isPrimary: false, sortOrder: 1 }

    const sentBody = () => {
      const call = fetchMock.mock.calls.find((c) => c[1]?.method === 'POST' || c[1]?.method === 'PUT')!
      return JSON.parse(call[1].body as string)
    }
    const contactGroup = (index: number) => screen.getByRole('group', { name: `Contacto ${index}` })

    it('has no contactName field any more', () => {
      render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

      expect(screen.queryByLabelText('Nombre de contacto')).not.toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Agregar contacto' })).toBeInTheDocument()
    })

    it('adds contacts, makes the first one primary and sends them in order on create', async () => {
      const user = userEvent.setup()
      render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

      await user.type(screen.getByLabelText('Nombre'), 'Acme')
      await user.click(screen.getByRole('button', { name: 'Agregar contacto' }))
      await user.type(within(contactGroup(1)).getByLabelText('Nombre'), 'Juana')
      await user.type(within(contactGroup(1)).getByLabelText('Apellido'), 'Pérez')
      await user.type(within(contactGroup(1)).getByLabelText('Teléfono'), '341-555')
      await user.click(screen.getByRole('button', { name: 'Agregar contacto' }))
      await user.type(within(contactGroup(2)).getByLabelText('Nombre'), 'Roberto')
      await user.type(within(contactGroup(2)).getByLabelText('Rol'), 'Dueño')
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'POST')).toBe(true))
      const body = sentBody()
      expect(body.contacts).toHaveLength(2)
      expect(body.contacts[0]).toMatchObject({
        firstName: 'Juana',
        lastName: 'Pérez',
        phone: '341-555',
        isPrimary: true,
        sortOrder: 0,
      })
      expect(body.contacts[1]).toMatchObject({ firstName: 'Roberto', role: 'Dueño', isPrimary: false, sortOrder: 1 })
      expect(body.contacts[0]).not.toHaveProperty('id')
    })

    it('requires a first name and blocks the request until it is filled', async () => {
      const user = userEvent.setup()
      render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)

      await user.type(screen.getByLabelText('Nombre'), 'Acme')
      await user.click(screen.getByRole('button', { name: 'Agregar contacto' }))
      await user.type(within(contactGroup(1)).getByLabelText('Teléfono'), '341-555')
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      expect(await screen.findByText('El nombre del contacto es obligatorio.')).toBeInTheDocument()
      expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'POST')).toBe(false)

      await user.type(within(contactGroup(1)).getByLabelText('Nombre'), 'Juana')
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))
      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'POST')).toBe(true))
    })

    it('keeps at most one primary contact', async () => {
      const user = userEvent.setup()
      render(<CustomerForm customer={{ ...customer, contacts: [juana, roberto] }} onSaved={vi.fn()} onCancel={vi.fn()} />)

      const primary1 = within(contactGroup(1)).getByRole('radio', { name: 'Principal' })
      const primary2 = within(contactGroup(2)).getByRole('radio', { name: 'Principal' })
      expect(primary1).toBeChecked()
      expect(primary2).not.toBeChecked()

      await user.click(primary2)
      expect(primary1).not.toBeChecked()
      expect(primary2).toBeChecked()
    })

    it('removes and reorders contacts, and sends the replace-set with ids and sort order on edit', async () => {
      const user = userEvent.setup()
      render(
        <CustomerForm
          customer={{ ...customer, contacts: [juana, roberto, { ...roberto, id: 'ct-3', firstName: 'Sofía', sortOrder: 2 }] }}
          onSaved={vi.fn()}
          onCancel={vi.fn()}
        />,
      )

      await user.click(screen.getByRole('button', { name: 'Bajar contacto 1' }))
      expect(within(contactGroup(1)).getByLabelText('Nombre')).toHaveValue('Roberto')
      await user.click(screen.getByRole('button', { name: 'Quitar contacto 3' }))
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'PUT')).toBe(true))
      const { contacts } = sentBody()
      expect(contacts.map((c: { id: string }) => c.id)).toEqual(['ct-2', 'ct-1'])
      expect(contacts.map((c: { sortOrder: number }) => c.sortOrder)).toEqual([0, 1])
      expect(contacts[1]).toMatchObject({ firstName: 'Juana', isPrimary: true })
    })

    it('clears all contacts on edit by sending an empty list', async () => {
      const user = userEvent.setup()
      render(<CustomerForm customer={{ ...customer, contacts: [juana] }} onSaved={vi.fn()} onCancel={vi.fn()} />)

      await user.click(screen.getByRole('button', { name: 'Quitar contacto 1' }))
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'PUT')).toBe(true))
      expect(sentBody().contacts).toEqual([])
    })
  })

  describe('optimistic concurrency', () => {
    const conflict = () =>
      new Response(JSON.stringify({ error: 'customer-modified' }), {
        status: 409,
        headers: { 'Content-Type': 'application/json' },
      })

    it('sends the updatedAtUtc it last read as expectedUpdatedAtUtc on update only', async () => {
      const user = userEvent.setup()
      render(<CustomerForm customer={customer} onSaved={vi.fn()} onCancel={vi.fn()} />)
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'PUT')).toBe(true))
      const put = fetchMock.mock.calls.find((c) => c[1]?.method === 'PUT')!
      expect(JSON.parse(put[1].body as string).expectedUpdatedAtUtc).toBe(customer.updatedAtUtc)
    })

    it('does not send it on create', async () => {
      const user = userEvent.setup()
      render(<CustomerForm onSaved={vi.fn()} onCancel={vi.fn()} />)
      await user.type(screen.getByLabelText('Nombre'), 'Jane')
      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      await waitFor(() => expect(fetchMock.mock.calls.some((c) => c[1]?.method === 'POST')).toBe(true))
      const post = fetchMock.mock.calls.find((c) => c[1]?.method === 'POST')!
      expect(JSON.parse(post[1].body as string)).not.toHaveProperty('expectedUpdatedAtUtc')
    })

    it('explains a 409 customer-modified in Spanish and reloads the customer on "Recargar"', async () => {
      const fresh = { ...customer, displayName: 'Changed elsewhere', updatedAtUtc: '2024-02-02T00:00:00Z' }
      fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') return conflict()
        if (url === `/customers/${customer.id}`) return new Response(JSON.stringify(fresh), { status: 200 })
        return new Response('[]', { status: 200 })
      })
      const onSaved = vi.fn()
      const onReload = vi.fn()
      const user = userEvent.setup()
      render(<CustomerForm customer={customer} onSaved={onSaved} onCancel={vi.fn()} onReload={onReload} />)

      await user.click(screen.getByRole('button', { name: /^guardar$/i }))

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent('Otra persona modificó este cliente mientras lo editabas.')
      expect(onSaved).not.toHaveBeenCalled()

      await user.click(within(alert).getByRole('button', { name: 'Recargar' }))

      await waitFor(() => expect(onReload).toHaveBeenCalledWith(fresh))
    })

    it('reports a failed reload instead of silently keeping the stale form', async () => {
      fetchMock.mockImplementation(async (_url: string, init?: RequestInit) => {
        if (init?.method === 'PUT') return conflict()
        throw new TypeError('Failed to fetch')
      })
      const user = userEvent.setup()
      render(<CustomerForm customer={customer} onSaved={vi.fn()} onCancel={vi.fn()} onReload={vi.fn()} />)

      await user.click(screen.getByRole('button', { name: /^guardar$/i }))
      await user.click(await screen.findByRole('button', { name: 'Recargar' }))

      expect(await screen.findByText('No se pudo recargar el cliente.')).toBeInTheDocument()
    })
  })
})
