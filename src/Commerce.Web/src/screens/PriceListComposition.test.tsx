import { render, screen, waitFor, within, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceListsScreen } from './PriceListsScreen'
import { todayIso, tomorrowIso } from '@/lib/isoDate'
import type { PriceListRecord } from '@/api/types'

const REPARTO = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
const MOSTRADOR = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
const COPY_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc'

const list = (id: string, name: string, extra: Partial<PriceListRecord> = {}): PriceListRecord => ({
  id,
  organizationId: 'org-1',
  branchId: 'branch-1',
  name,
  isDefault: false,
  createdAtUtc: '2026-10-01T00:00:00Z',
  createdByUserId: 'user-1',
  floorPriceListId: null,
  ...extra,
})
const reparto = list(REPARTO, 'Reparto')
const mostrador = list(MOSTRADOR, 'Mostrador', { isDefault: true, floorPriceListId: REPARTO })

const mostradorComponents = [
  { code: 'IVA', label: 'IVA', percentage: 10.5, calculationBase: 'Base', order: 1 },
  { code: 'IB', label: 'IB', percentage: 2.5, calculationBase: 'Base', order: 2 },
  { code: 'REMARCACION', label: 'Remarcación', percentage: 35, calculationBase: 'Base', order: 3 },
]

const breakdown = (id: string, name: string) => ({
  priceListId: id,
  priceListName: name,
  on: '2026-10-02',
  floorPriceListId: id === MOSTRADOR ? REPARTO : null,
  composition: { source: 'list', effectiveFrom: '2026-10-02', components: mostradorComponents, history: [] },
  items: [
    {
      presentationId: 'p1',
      productId: 'prod1',
      productName: 'Bola de lomo',
      presentationName: 'Kilo',
      identificationCode: '1001',
      entryEffectiveFrom: '2026-10-02',
      base: 11400,
      components: [
        { ...mostradorComponents[0], calculationAmount: 11400, amount: 1197 },
        { ...mostradorComponents[1], calculationAmount: 11400, amount: 285 },
        { ...mostradorComponents[2], calculationAmount: 11400, amount: 3990 },
      ],
      final: 16872,
    },
  ],
})

const composition = {
  source: 'list',
  effectiveFrom: '2026-10-02',
  components: mostradorComponents,
  history: [
    { id: 'v1', effectiveFrom: '2026-10-02', components: mostradorComponents },
    { id: 'v0', effectiveFrom: '2026-09-01', components: [mostradorComponents[0]] },
  ],
}

const violation = {
  priceListId: MOSTRADOR,
  priceListName: 'Mostrador',
  floorPriceListId: REPARTO,
  floorPriceListName: 'Reparto',
  presentationId: 'p1',
  productId: 'prod1',
  productName: 'Bola de lomo',
  presentationName: 'Kilo',
  price: 12000,
  floorPrice: 16530,
}

type Handler = (init?: RequestInit) => Response
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

describe('price list composition screens', () => {
  const fetchMock = vi.fn()
  let routes: Record<string, Handler>

  beforeEach(() => {
    routes = {
      'GET /pricing/price-lists': () => json([reparto, mostrador]),
      'GET /catalog/presentations': () => json([]),
      [`GET /pricing/price-lists/${MOSTRADOR}/breakdown`]: () => json(breakdown(MOSTRADOR, 'Mostrador')),
      [`GET /pricing/price-lists/${MOSTRADOR}/composition`]: () => json(composition),
      [`GET /pricing/price-lists/${COPY_ID}/breakdown`]: () => json(breakdown(COPY_ID, 'Mostrador 40')),
      [`GET /pricing/price-lists/${COPY_ID}/composition`]: () => json(composition),
    }
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`
      const handler = routes[key]
      if (!handler) throw new Error(`unrouted ${key}`)
      return handler(init)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const callsTo = (key: string) =>
    fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)

  async function openRow(buttonName: string) {
    const user = userEvent.setup()
    render(<PriceListsScreen />)
    const row = await screen.findByRole('row', { name: /Mostrador/ })
    await user.click(within(row).getByRole('button', { name: buttonName }))
    return user
  }

  it('breaks the price down into base, each component and the final price', async () => {
    await openRow('Composición')

    expect(await screen.findByRole('heading', { name: 'Composición de Mostrador' })).toBeInTheDocument()
    expect(screen.getByText('IVA 10,5 % + IB 2,5 % + Remarcación 35 % sobre base')).toBeInTheDocument()
    expect(screen.getByText('Piso: Reparto')).toBeInTheDocument()

    const row = await screen.findByRole('row', { name: /Bola de lomo/ })
    for (const text of ['$ 11.400,00', '$ 1.197,00', '$ 285,00', '$ 3.990,00', '$ 16.872,00']) {
      expect(within(row).getByText(text)).toBeInTheDocument()
    }
    expect(screen.getByText(/Historial de composiciones/)).toBeInTheDocument()
    expect(screen.getByText(/01\/09\/2026/)).toBeInTheDocument()
  })

  it('re-reads the breakdown for the chosen date', async () => {
    await openRow('Composición')
    await screen.findByRole('row', { name: /Bola de lomo/ })

    fireEvent.change(screen.getByLabelText('Precios al día'), { target: { value: '2026-11-01' } })

    await waitFor(() =>
      expect(
        callsTo(`GET /pricing/price-lists/${MOSTRADOR}/breakdown`).some(([url]) => String(url).includes('on=2026-11-01')),
      ).toBe(true),
    )
  })

  it('lists the products below the floor when a composition publish is refused', async () => {
    routes[`POST /pricing/price-lists/${MOSTRADOR}/composition`] = () =>
      json({ error: 'price-below-floor', violations: [violation] }, 409)
    const user = await openRow('Composición')
    await user.click(await screen.findByRole('button', { name: 'Editar composición' }))

    expect(await screen.findByRole('heading', { name: 'Editar composición de Mostrador' })).toBeInTheDocument()
    expect(screen.getByLabelText('Vigente desde')).toHaveValue(tomorrowIso())
    expect(tomorrowIso() > todayIso()).toBe(true)
    await user.click(screen.getByLabelText('Solo remarcación'))
    await user.type(screen.getByLabelText('Remarcación %'), '20')
    await user.click(screen.getByRole('button', { name: 'Publicar composición' }))

    expect(await screen.findByText('Estos productos quedarían por debajo de Reparto')).toBeInTheDocument()
    const row = screen.getByRole('row', { name: /Bola de lomo/ })
    expect(within(row).getByText('$ 12.000,00')).toBeInTheDocument()
    expect(within(row).getByText('$ 16.530,00')).toBeInTheDocument()
    const [, init] = callsTo(`POST /pricing/price-lists/${MOSTRADOR}/composition`)[0]
    expect(JSON.parse(init.body as string)).toEqual({ effectiveFrom: tomorrowIso(), remarcacionPercentage: 20 })
  })

  it('publishes the edited components and explains a date that is already taken', async () => {
    routes[`POST /pricing/price-lists/${MOSTRADOR}/composition`] = () =>
      json({ error: 'composition-already-exists-for-date' }, 409)
    const user = await openRow('Composición')
    await user.click(await screen.findByRole('button', { name: 'Editar composición' }))
    const percentage = await screen.findByLabelText('Porcentaje 3')
    expect(percentage).toHaveValue(35)
    await user.clear(percentage)
    await user.type(percentage, '30')
    await user.click(screen.getByRole('button', { name: 'Publicar composición' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una composición con esa fecha de vigencia')
    const [, init] = callsTo(`POST /pricing/price-lists/${MOSTRADOR}/composition`)[0]
    const body = JSON.parse(init.body as string)
    expect(body.components).toHaveLength(3)
    expect(body.components[2]).toEqual({
      code: 'REMARCACION',
      label: 'Remarcación',
      percentage: 30,
      calculationBase: 'Base',
      order: 3,
    })
  })

  it('copies a list with a new markup and opens the new list', async () => {
    routes[`POST /pricing/price-lists/${MOSTRADOR}/copy`] = () =>
      json({ priceList: list(COPY_ID, 'Mostrador 40', { floorPriceListId: REPARTO }), entriesCopied: 1, composition }, 201)
    const user = await openRow('Duplicar lista')

    expect(await screen.findByRole('heading', { name: 'Duplicar Mostrador' })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Nombre de la nueva lista'), 'Mostrador 40')
    await user.type(screen.getByLabelText('Remarcación %'), '40')
    await user.click(screen.getByRole('button', { name: 'Duplicar lista' }))

    expect(await screen.findByRole('heading', { name: 'Composición de Mostrador 40' })).toBeInTheDocument()
    const [, init] = callsTo(`POST /pricing/price-lists/${MOSTRADOR}/copy`)[0]
    expect(JSON.parse(init.body as string)).toEqual({
      name: 'Mostrador 40',
      effectiveFrom: todayIso(),
      remarcacionPercentage: 40,
    })
  })

  it('says when the copy name is taken and shows floor violations', async () => {
    routes[`POST /pricing/price-lists/${MOSTRADOR}/copy`] = () => json({ error: 'price-list-name-taken' }, 409)
    const user = await openRow('Duplicar lista')
    await user.type(await screen.findByLabelText('Nombre de la nueva lista'), 'Reparto')
    await user.type(screen.getByLabelText('Remarcación %'), '10')
    await user.click(screen.getByRole('button', { name: 'Duplicar lista' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una lista con ese nombre')

    routes[`POST /pricing/price-lists/${MOSTRADOR}/copy`] = () =>
      json({ error: 'price-below-floor', violations: [violation] }, 409)
    await user.click(screen.getByRole('button', { name: 'Duplicar lista' }))
    expect(await screen.findByText('Estos productos quedarían por debajo de Reparto')).toBeInTheDocument()
  })

  it('changes the floor list and explains a cycle', async () => {
    routes[`PUT /pricing/price-lists/${MOSTRADOR}/floor`] = () => json({ ...mostrador, floorPriceListId: null })
    const user = await openRow('Composición')
    const select = await screen.findByLabelText('Lista piso')
    expect(select).toHaveValue(REPARTO)
    expect(within(select).queryByRole('option', { name: 'Mostrador' })).not.toBeInTheDocument()

    await user.selectOptions(select, '')
    await waitFor(() => expect(select).toHaveValue(''))
    expect(screen.queryByText('Piso: Reparto')).not.toBeInTheDocument()
    const [, init] = callsTo(`PUT /pricing/price-lists/${MOSTRADOR}/floor`)[0]
    expect(JSON.parse(init.body as string)).toEqual({ floorPriceListId: null })

    routes[`PUT /pricing/price-lists/${MOSTRADOR}/floor`] = () => json({ error: 'floor-cycle' }, 409)
    await user.selectOptions(select, REPARTO)
    expect(await screen.findByRole('alert')).toHaveTextContent('ciclo')
  })
})
