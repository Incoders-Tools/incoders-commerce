import { render, screen, waitFor, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SupplierForm } from './SupplierForm'
import { categoryEntry, json, supplierFixture } from './supplierFixtures'

const NO_ID = '00000000-0000-0000-0000-000000000000'

describe('SupplierForm', () => {
  const fetchMock = vi.fn()
  const onSaved = vi.fn()
  const onCancel = vi.fn()
  const onReload = vi.fn()
  const categories = [categoryEntry('cat-carne', 'Carne'), categoryEntry('cat-limp', 'Limpieza', false)]

  beforeEach(() => {
    fetchMock.mockImplementation(async (_url: string, init?: RequestInit) =>
      init?.method === 'POST' ? json({ supplierId: 'new-id' }, 201) : json(supplierFixture()),
    )
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    onSaved.mockReset()
    onCancel.mockReset()
    onReload.mockReset()
  })

  const renderForm = (supplier?: ReturnType<typeof supplierFixture>) =>
    render(
      <MemoryRouter>
        <SupplierForm
          supplier={supplier}
          categories={categories}
          onSaved={onSaved}
          onCancel={onCancel}
          onReload={onReload}
        />
      </MemoryRouter>,
    )

  const lastBody = () => JSON.parse(fetchMock.mock.calls.at(-1)![1].body as string)

  it('groups the fields into Datos, Contacto, Ubicación, Fiscal, Comercial and Observaciones', () => {
    renderForm()
    for (const name of ['Datos', 'Contacto', 'Personas de contacto', 'Ubicación', 'Fiscal', 'Comercial', 'Observaciones']) {
      expect(screen.getByRole('group', { name })).toBeInTheDocument()
    }
  })

  it('creates a supplier with only a name', async () => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Limpieza SRL')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/suppliers')
    expect(init.method).toBe('POST')
    expect(lastBody()).toMatchObject({ displayName: 'Limpieza SRL', taxIdType: 'None', taxId: null, paymentTermsDays: null })
    expect(lastBody()).not.toHaveProperty('contacts')
    expect(lastBody()).not.toHaveProperty('expectedUpdatedAtUtc')
  })

  it('sends category, payment terms and bank data as typed', async () => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    await user.selectOptions(screen.getByLabelText('Rubro'), 'cat-carne')
    fireEvent.change(screen.getByLabelText('Plazo de pago (días)'), { target: { value: '45' } })
    fireEvent.change(screen.getByLabelText('CBU/CVU'), { target: { value: '0110599520000012345678' } })
    fireEvent.change(screen.getByLabelText('Alias'), { target: { value: 'norte.carnes' } })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(lastBody()).toMatchObject({
      categoryId: 'cat-carne',
      paymentTermsDays: 45,
      bankCbu: '0110599520000012345678',
      bankAlias: 'norte.carnes',
    })
  })

  it.each([
    ['CBU/CVU', '123', 'El CBU/CVU debe tener 22 dígitos.'],
    ['Alias', 'abc', 'El alias debe tener entre 6 y 20 caracteres.'],
    ['Plazo de pago (días)', '400', 'El plazo de pago debe estar entre 0 y 365 días.'],
  ])('blocks an invalid %s before calling the API', async (label, value, message) => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    fireEvent.change(screen.getByLabelText(label), { target: { value } })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('accepts a CBU written with separators', async () => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    fireEvent.change(screen.getByLabelText('CBU/CVU'), { target: { value: '0110 5995 2000 0012 3456 78' } })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
  })

  it('requires 11 digits for a CUIT', async () => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    await user.selectOptions(screen.getByLabelText('Tipo de identificación fiscal'), 'Cuit')
    fireEvent.change(screen.getByLabelText('CUIT/CUIL'), { target: { value: '30-1234' } })
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('El CUIT/CUIL debe tener 11 dígitos.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('PUTs the replace-set with the concurrency token and clears city and category with the empty id', async () => {
    const user = userEvent.setup()
    renderForm(supplierFixture({ cityId: null, cityName: null, provinceName: null, categoryId: null, categoryName: null }))

    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/suppliers/11111111-1111-1111-1111-111111111111')
    expect(init.method).toBe('PUT')
    expect(lastBody()).toMatchObject({
      cityId: NO_ID,
      categoryId: NO_ID,
      isEnabled: true,
      expectedUpdatedAtUtc: '2024-01-01T00:00:00Z',
    })
    expect(lastBody().contacts.map((c: { firstName: string; sortOrder: number }) => [c.firstName, c.sortOrder])).toEqual([
      ['Ana', 0],
      ['Juana', 1],
    ])
  })

  it('offers active categories plus the current one even when inactive', () => {
    renderForm(supplierFixture({ categoryId: 'cat-limp', categoryName: 'Limpieza' }))
    const options = Array.from((screen.getByLabelText('Rubro') as HTMLSelectElement).options).map((o) => o.text)
    expect(options).toEqual(['Sin rubro', 'Carne', 'Limpieza (inactiva)'])
  })

  it('links an existing supplier to its current account', () => {
    renderForm(supplierFixture())
    expect(screen.getByRole('link', { name: 'Cuenta corriente' })).toHaveAttribute(
      'href',
      '/app/suppliers/11111111-1111-1111-1111-111111111111/account',
    )
  })

  it('does not link a supplier that does not exist yet', () => {
    renderForm()
    expect(screen.queryByRole('link', { name: 'Cuenta corriente' })).not.toBeInTheDocument()
  })

  it('explains a 409 supplier-modified and reloads on "Recargar"', async () => {
    fetchMock.mockImplementation(async (_url: string, init?: RequestInit) =>
      init?.method === 'PUT' ? json({ error: 'supplier-modified' }, 409) : json(supplierFixture({ displayName: 'Fresco' })),
    )
    const user = userEvent.setup()
    renderForm(supplierFixture())

    await user.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/modificó este proveedor/i)
    await user.click(screen.getByRole('button', { name: 'Recargar' }))

    await waitFor(() => expect(onReload).toHaveBeenCalledWith(expect.objectContaining({ displayName: 'Fresco' })))
    expect(onSaved).not.toHaveBeenCalled()
  })

  it('shows the server message for a 400', async () => {
    fetchMock.mockImplementation(async () => json({ title: 'bankAlias must be 6-20 characters' }, 400))
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('bankAlias must be 6-20 characters')
  })

  it('checks the supplier email while typing and blocks the submit while it is invalid', async () => {
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    const email = screen.getByLabelText('Correo electrónico')
    await user.type(email, 'ventas@norte')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(email).toHaveAttribute('aria-invalid', 'true')
    expect(fetchMock).not.toHaveBeenCalled()

    await user.type(email, '.com.ar')
    expect(email).toHaveAccessibleDescription('Correo electrónico válido')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(lastBody().email).toBe('ventas@norte.com.ar')
  })

  it('puts a server-refused email on the email field', async () => {
    fetchMock.mockImplementation(async () =>
      json({ title: 'One or more validation errors occurred.', errors: { email: ['email must be a valid email address.'] } }, 400),
    )
    const user = userEvent.setup()
    renderForm()

    await user.type(screen.getByLabelText('Nombre'), 'Norte')
    await user.type(screen.getByLabelText('Correo electrónico'), 'ventas@norte.com')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByLabelText('Correo electrónico')).toHaveAttribute('aria-invalid', 'true'))
  })
})
