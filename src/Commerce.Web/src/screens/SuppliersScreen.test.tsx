import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SuppliersScreen } from './SuppliersScreen'
import type { SupplierRecord } from '@/api/types'
import { categoryEntry, json, supplierFixture } from './supplierFixtures'

const geoCity = (id: string, name: string, provinceName: string) => ({
  id, indecId: null, name, provinceId: '82', provinceName, countryCode: 'AR', departmentName: null,
  isActive: true, createdAtUtc: '2024-01-01T00:00:00Z', updatedAtUtc: '2024-01-01T00:00:00Z',
})

describe('SuppliersScreen', () => {
  const fetchMock = vi.fn()
  let suppliers: SupplierRecord[]
  let balances: { supplierId: string; balance: number; overdue: number }[]

  const listCalls = () =>
    fetchMock.mock.calls
      .map((call) => call[0] as string)
      .filter((url) => url === '/suppliers' || url.startsWith('/suppliers?'))

  beforeEach(() => {
    suppliers = [supplierFixture()]
    balances = [{ supplierId: supplierFixture().id, balance: 60000, overdue: 0 }]
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/geo/cities')) return json([geoCity('city-rosario', 'Rosario', 'Santa Fe')])
      if (url.startsWith('/suppliers/categories')) return json([categoryEntry('cat-carne', 'Carne'), categoryEntry('cat-tec', 'Tecnología')])
      if (url === '/suppliers/account/balances') return json(balances)
      if (init?.method === 'POST') return json({ supplierId: 'new' }, 201)
      return json(suppliers)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const renderScreen = () =>
    render(
      <MemoryRouter>
        <SuppliersScreen />
      </MemoryRouter>,
    )

  it('lists suppliers with category, primary contact, city, tax id and a formatted balance', async () => {
    renderScreen()

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Frigorífico Norte')).toBeInTheDocument()
    expect(within(row).getByText('Carne')).toBeInTheDocument()
    expect(within(row).getByText('Juana Pérez')).toBeInTheDocument()
    expect(within(row).getByText('Rosario — Santa Fe')).toBeInTheDocument()
    expect(within(row).getByText('30-12345678-9')).toBeInTheDocument()
    await waitFor(() => expect(within(row).getByText(/60\.000,00/)).toBeInTheDocument())
    expect(within(row).queryByText(/vencido/i)).not.toBeInTheDocument()
  })

  it('highlights an overdue balance from the balances endpoint', async () => {
    balances = [{ supplierId: supplierFixture().id, balance: 60000, overdue: 25000 }]
    renderScreen()

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(await within(row).findByText(/vencido.*25\.000,00/i)).toBeInTheDocument()
  })

  it('words a negative balance as in our favour', async () => {
    suppliers = [supplierFixture({ balance: -5000 })]
    balances = [{ supplierId: supplierFixture().id, balance: -5000, overdue: 0 }]
    renderScreen()

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(await within(row).findByText(/a favor/i)).toBeInTheDocument()
  })

  it('still lists suppliers when the balances cannot be read', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url === '/suppliers/account/balances') throw new TypeError('down')
      if (url.startsWith('/suppliers/categories')) return json([])
      return json(suppliers)
    })
    renderScreen()
    expect(await screen.findByText('Frigorífico Norte')).toBeInTheDocument()
  })

  it('shows an empty state and a load error apart', async () => {
    suppliers = []
    const { unmount } = renderScreen()
    await screen.findByText('Todavía no hay proveedores.')
    unmount()

    fetchMock.mockImplementation(async (url: string) => {
      if (url === '/suppliers') throw new TypeError('down')
      return json([])
    })
    renderScreen()
    await screen.findByRole('alert')
    expect(screen.queryByText('Todavía no hay proveedores.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/no se pudieron cargar los proveedores/i)
  })

  it('links each row to its current account', async () => {
    renderScreen()
    await screen.findByText('Frigorífico Norte')
    expect(screen.getByRole('link', { name: 'Cuenta corriente' })).toHaveAttribute(
      'href',
      '/app/suppliers/11111111-1111-1111-1111-111111111111/account',
    )
  })

  it('searches on the server, debounced', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('Frigorífico Norte')

    suppliers = [supplierFixture({ id: '2', displayName: 'Limpiex' })]
    await user.type(screen.getByLabelText('Buscar proveedores'), 'limp')

    await screen.findByText('Limpiex')
    expect(listCalls()).toEqual(['/suppliers', '/suppliers?search=limp'])
  })

  it('filters by category, enabled state and city on the server', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('Frigorífico Norte')

    await user.selectOptions(await screen.findByLabelText('Rubro'), 'cat-tec')
    await waitFor(() => expect(listCalls().at(-1)).toBe('/suppliers?categoryId=cat-tec'))

    await user.selectOptions(screen.getByLabelText('Estado'), 'enabled')
    await waitFor(() => expect(listCalls().at(-1)).toBe('/suppliers?categoryId=cat-tec&enabled=true'))

    await user.click(screen.getByRole('combobox', { name: 'Ciudad' }))
    await user.click(await screen.findByRole('option', { name: 'Rosario — Santa Fe' }))
    await waitFor(() =>
      expect(listCalls().at(-1)).toBe('/suppliers?categoryId=cat-tec&cityId=city-rosario&enabled=true'),
    )
  })

  it('creates a supplier from the full-page form and refreshes the list', async () => {
    suppliers = []
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('Todavía no hay proveedores.')

    await user.click(screen.getByRole('button', { name: 'Nuevo proveedor' }))
    await user.type(screen.getByLabelText('Nombre'), 'Limpiex')
    suppliers = [supplierFixture({ displayName: 'Limpiex' })]
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Limpiex')
    expect(screen.getByRole('heading', { name: 'Proveedores' })).toBeInTheDocument()
  })

  it('opens the edit form for a listed supplier', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('Frigorífico Norte')

    await user.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByText('Editar proveedor')).toBeInTheDocument()
  })

  it('switches to cards and remembers the preference', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('Frigorífico Norte')

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)
    expect(window.localStorage.getItem('view:suppliers')).toBe('cards')
  })
})
