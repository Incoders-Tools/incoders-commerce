import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import type { StockLevel } from '@/api/types'
import { StockScreen } from './StockScreen'
import { json } from './supplierFixtures'
import { catalogPresentation, catalogProduct } from './receptionFixtures'

const level = (overrides: Partial<StockLevel> = {}): StockLevel => ({
  presentationId: 'pr-1',
  productId: 'p-1',
  productName: 'Media res',
  presentationName: 'Kilo',
  quantityBehavior: 'Weighted',
  unitId: 'unit-1',
  identificationCode: null,
  onHand: 120,
  minimumQuantity: null,
  belowMinimum: false,
  shortfall: null,
  lastMovementAtUtc: '2026-10-01T12:00:00Z',
  ...overrides,
})

describe('StockScreen', () => {
  const fetchMock = vi.fn()
  let rows: StockLevel[]

  const calls = (method: string, matcher: (url: string) => boolean) =>
    fetchMock.mock.calls.filter(([url, init]) => (init?.method ?? 'GET') === method && matcher(url as string))

  beforeEach(() => {
    rows = [
      level(),
      level({
        presentationId: 'pr-2',
        productName: 'Chorizo',
        presentationName: 'Paquete',
        quantityBehavior: 'FixedQuantity',
        onHand: 3,
        minimumQuantity: 10,
        belowMinimum: true,
        shortfall: 7,
      }),
      level({ presentationId: 'pr-3', productName: 'Vacío', onHand: 0, minimumQuantity: 5, belowMinimum: true, shortfall: 5 }),
      level({ presentationId: 'pr-4', productName: 'Costilla', onHand: -2.5, minimumQuantity: 0, belowMinimum: true, shortfall: 2.5 }),
    ]
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/catalog/products') return json([catalogProduct('p-1', 'Media res')])
      if (url === '/catalog/presentations') return json([catalogPresentation('pr-1', 'p-1', 'Kilo', 1)])
      if (url === '/stock/adjustments') return json({ movement: {}, onHand: 117.5 }, 201)
      if (url.startsWith('/stock/minimums/')) {
        return json({ presentationId: 'pr-1', minimumQuantity: JSON.parse(init!.body as string).minimumQuantity, updatedAtUtc: null })
      }
      if (url.startsWith('/stock')) return json(rows)
      throw new Error(`unexpected ${url}`)
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
        <StockScreen />
      </MemoryRouter>,
    )

  const rowOf = async (name: string) => {
    const table = await screen.findByRole('table')
    return within(table)
      .getAllByRole('row')
      .find((row) => within(row).queryByText(name) !== null)!
  }

  it('lists on-hand quantities with their unit and flags what is below minimum, out and negative', async () => {
    renderScreen()

    const media = await rowOf('Media res')
    expect(within(media).getByText('120 kg')).toBeInTheDocument()
    expect(within(media).getByText('Normal')).toBeInTheDocument()

    const chorizo = await rowOf('Chorizo')
    expect(within(chorizo).getByText('3 u')).toBeInTheDocument()
    expect(within(chorizo).getByText('10 u')).toBeInTheDocument()
    expect(within(chorizo).getByText('Bajo mínimo')).toBeInTheDocument()

    expect(within(await rowOf('Vacío')).getByText('Sin stock')).toBeInTheDocument()
    const costilla = await rowOf('Costilla')
    expect(within(costilla).getByText('Negativo')).toBeInTheDocument()
    expect(within(costilla).getByText('-2,5 kg')).toBeInTheDocument()
  })

  it('asks the server for only what is below minimum and for the typed search', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByRole('table')

    await user.click(screen.getByRole('checkbox', { name: 'Solo bajo mínimo' }))
    await waitFor(() => expect(calls('GET', (url) => url === '/stock?onlyBelowMinimum=true')).toHaveLength(1))

    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'chori' } })
    await waitFor(() =>
      expect(calls('GET', (url) => url === '/stock?search=chori&onlyBelowMinimum=true')).toHaveLength(1),
    )
  })

  it('edits a minimum inline and clears it with an empty value', async () => {
    const user = userEvent.setup()
    renderScreen()

    const media = await rowOf('Media res')
    await user.click(within(media).getByRole('button', { name: 'Mínimo' }))
    fireEvent.change(screen.getByLabelText('Mínimo de Media res — Kilo'), { target: { value: '50' } })
    await user.click(screen.getByRole('button', { name: 'Guardar mínimo' }))

    await waitFor(() => expect(calls('PUT', (url) => url === '/stock/minimums/pr-1')).toHaveLength(1))
    expect(JSON.parse(calls('PUT', (url) => url === '/stock/minimums/pr-1')[0][1].body)).toEqual({ minimumQuantity: 50 })
    await waitFor(() => expect(screen.queryByLabelText('Mínimo de Media res — Kilo')).not.toBeInTheDocument())

    const chorizo = await rowOf('Chorizo')
    await user.click(within(chorizo).getByRole('button', { name: 'Mínimo' }))
    fireEvent.change(screen.getByLabelText('Mínimo de Chorizo — Paquete'), { target: { value: '' } })
    await user.click(screen.getByRole('button', { name: 'Guardar mínimo' }))
    await waitFor(() => expect(calls('PUT', (url) => url === '/stock/minimums/pr-2')).toHaveLength(1))
    expect(JSON.parse(calls('PUT', (url) => url === '/stock/minimums/pr-2')[0][1].body)).toEqual({ minimumQuantity: null })
  })

  it('refuses a negative minimum without calling the API', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(within(await rowOf('Media res')).getByRole('button', { name: 'Mínimo' }))
    fireEvent.change(screen.getByLabelText('Mínimo de Media res — Kilo'), { target: { value: '-3' } })
    await user.click(screen.getByRole('button', { name: 'Guardar mínimo' }))

    expect(await screen.findByText('Ingresá un número mayor o igual a cero.')).toBeInTheDocument()
    expect(calls('PUT', () => true)).toHaveLength(0)
  })

  it('links each row to the movements of its presentation', async () => {
    renderScreen()

    const link = within(await rowOf('Media res')).getByRole('link', { name: 'Movimientos' })
    expect(link).toHaveAttribute('href', '/app/stock/pr-1/movements')
  })

  it('registers a shrinkage as a negative quantity with its reason', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(within(await rowOf('Media res')).getByRole('button', { name: 'Ajustar' }))
    fireEvent.change(screen.getByLabelText('Tipo de ajuste'), { target: { value: 'Shrinkage' } })
    expect(screen.getByText(/siempre resta del stock/i)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Cantidad'), { target: { value: '2,5' } })
    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'Se venció' } })
    await user.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText(/Ajuste registrado\. Stock actual: 117,5 kg/)).toBeInTheDocument()
    const [, init] = calls('POST', (url) => url === '/stock/adjustments')[0]
    expect(JSON.parse(init.body as string)).toEqual({
      presentationId: 'pr-1',
      kind: 'Shrinkage',
      quantity: -2.5,
      reason: 'Se venció',
    })
  })

  it('makes the operator choose add or subtract for an Ajuste, with no default', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Ajustar stock' }))
    await user.click(screen.getByRole('combobox', { name: 'Presentación' }))
    await user.click(await screen.findByRole('option', { name: /Media res — Kilo/ }))
    fireEvent.change(screen.getByLabelText('Tipo de ajuste'), { target: { value: 'Adjustment' } })

    const add = screen.getByRole('radio', { name: 'Suma' })
    const subtract = screen.getByRole('radio', { name: 'Resta' })
    expect(add).not.toBeChecked()
    expect(subtract).not.toBeChecked()

    fireEvent.change(screen.getByLabelText('Cantidad'), { target: { value: '4' } })
    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'Conteo' } })
    await user.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    expect(await screen.findByText('Elegí si el ajuste suma o resta.')).toBeInTheDocument()
    expect(calls('POST', (url) => url === '/stock/adjustments')).toHaveLength(0)

    await user.click(subtract)
    await user.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    await waitFor(() => expect(calls('POST', (url) => url === '/stock/adjustments')).toHaveLength(1))
    expect(JSON.parse(calls('POST', (url) => url === '/stock/adjustments')[0][1].body)).toMatchObject({
      kind: 'Adjustment',
      quantity: -4,
    })
  })

  it('registers an opening stock as a positive quantity and needs no direction', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(within(await rowOf('Chorizo')).getByRole('button', { name: 'Ajustar' }))
    fireEvent.change(screen.getByLabelText('Tipo de ajuste'), { target: { value: 'Opening' } })
    expect(screen.queryByRole('radio', { name: 'Suma' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Cantidad'), { target: { value: '12,5' } })
    fireEvent.change(screen.getByLabelText('Motivo'), { target: { value: 'Carga inicial' } })
    await user.click(screen.getByRole('button', { name: 'Registrar ajuste' }))

    expect(await screen.findByText('Esta presentación se cuenta en unidades enteras.')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Cantidad'), { target: { value: '12' } })
    await user.click(screen.getByRole('button', { name: 'Registrar ajuste' }))
    await waitFor(() => expect(calls('POST', (url) => url === '/stock/adjustments')).toHaveLength(1))
    expect(JSON.parse(calls('POST', (url) => url === '/stock/adjustments')[0][1].body)).toMatchObject({
      presentationId: 'pr-2',
      kind: 'Opening',
      quantity: 12,
    })
  })

  it('tells the operator to pick a branch instead of calling the API', async () => {
    render(
      <MemoryRouter>
        <BranchContext.Provider value={{ selectedBranch: null, selectableBranches: [], selectBranch: () => {} }}>
          <StockScreen />
        </BranchContext.Provider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/Elegí una sucursal/)).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
