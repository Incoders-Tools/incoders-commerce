import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import type { DeliveryRunDetail, OrderTrackingDetail, OrderTrackingSummary, RemitoDocument } from '@/api/fulfillment'
import { OrdersScreen } from './OrdersScreen'
import { OrderTrackingScreen } from './OrderTrackingScreen'
import { DeliveryRunFormScreen } from './DeliveryRunFormScreen'
import { DeliveryRunScreen } from './DeliveryRunScreen'
import { DeliveryRunSettleScreen } from './DeliveryRunSettleScreen'
import { RemitoPrintScreen } from './RemitoPrintScreen'
import { OrganizationDocumentsForm } from './OrganizationDocumentsForm'

/**
 * order-fulfillment-and-delivery, web: the orders tracking list and its bulk actions, one order's manual steps, building
 * a run from selected orders, settling it, the remitos printed in duplicate, and the organization's document data.
 */
describe('fulfillment screens', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  function routeFetch(routes: Record<string, (init?: RequestInit) => Response>) {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      const handler = routes[key] ?? routes[`${init?.method ?? 'GET'} ${url.split('?')[0]}`]
      if (!handler) throw new TypeError(`unrouted ${key}`)
      return handler(init)
    })
  }

  const callsTo = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)

  const order = (overrides: Partial<OrderTrackingSummary> = {}): OrderTrackingSummary => ({
    orderId: 'o-1',
    orderNumber: 'P01-W-12',
    submittedAtUtc: '2026-10-05T13:00:00Z',
    origin: 'RegisteredCustomer',
    customerId: 'c-1',
    customerName: 'Parrilla Don Julio',
    status: 'ReadyToDispatch',
    total: 26500,
    lineCount: 2,
    runId: null,
    runNumber: null,
    runDate: null,
    remitoNumber: null,
    deliveredTotal: null,
    settlement: null,
    deliveredAtUtc: null,
    note: null,
    cancelReason: null,
    ...overrides,
  })

  const detail = (overrides: Partial<OrderTrackingSummary> = {}, allowed: OrderTrackingDetail['allowedTransitions'] = []): OrderTrackingDetail => ({
    summary: order(overrides),
    lines: [
      { lineNo: 1, presentationId: 'p-1', productName: 'Vacío', presentationName: 'Por kg', quantityBehavior: 'Weighted', quantity: 2.5, unitNetPrice: 10000, lineTotal: 25000, deliveredQuantity: null },
      { lineNo: 2, presentationId: 'p-2', productName: 'Chorizo', presentationName: 'Unidad', quantityBehavior: 'FixedQuantity', quantity: 3, unitNetPrice: 500, lineTotal: 1500, deliveredQuantity: null },
    ],
    party: { displayName: 'Parrilla Don Julio', legalName: 'Don Julio SRL', taxIdType: 'Cuit', taxId: '30712345671', taxCondition: 'ResponsableInscripto', phone: '2478-555555', address: 'Frascheri 626', locality: 'Arrecifes', province: 'Buenos Aires', postalCode: '2740', deliveryNotes: 'Portón verde' },
    allowedTransitions: allowed,
  })

  function LocationProbe() {
    const location = useLocation()
    return <p data-testid="location">{`${location.pathname}${location.search}`}</p>
  }

  const renderAt = (path: string, element: React.ReactNode, pattern = '*') =>
    render(
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path={pattern} element={<>{element}<LocationProbe /></>} />
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>,
    )

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    vi.stubGlobal('print', vi.fn())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists the orders in progress and sends the selected ones to print or to a new run', async () => {
    routeFetch({
      'GET /orders/tracking': () =>
        json([order(), order({ orderId: 'o-2', orderNumber: 'P01-W-13', customerName: 'Almacén Sur', status: 'Cancelled' })]),
    })
    const user = userEvent.setup()
    renderAt('/app/orders', <OrdersScreen />)

    await screen.findByText('P01-W-12')
    expect(fetchMock.mock.calls[0][0]).toBe('/orders/tracking?status=Active')
    expect(screen.getByRole('link', { name: 'Tomar pedido' })).toHaveAttribute('href', '/app/orders/new')

    await user.click(screen.getByLabelText('Seleccionar todos'))
    expect(screen.getByText('2 pedidos seleccionados')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Imprimir remitos' }))
    // A cancelled order has no remito.
    expect(screen.getByTestId('location')).toHaveTextContent('/print/remitos?orders=o-1')
  })

  it('builds a run from the selected orders that can still join one', async () => {
    routeFetch({ 'GET /orders/tracking': () => json([order(), order({ orderId: 'o-2', orderNumber: 'P01-W-13', runId: 'r-9', runNumber: 9 })]) })
    const user = userEvent.setup()
    renderAt('/app/orders', <OrdersScreen />)

    await user.click(await screen.findByLabelText('Seleccionar P01-W-12'))
    await user.click(screen.getByLabelText('Seleccionar P01-W-13'))
    await user.click(screen.getByRole('button', { name: 'Armar reparto' }))

    expect(screen.getByTestId('location')).toHaveTextContent('/app/deliveries/new?orders=o-1')
  })

  it('offers the allowed manual steps of an order and cancels only with a reason', async () => {
    let current = detail({ status: 'Confirmed' }, ['InPreparation', 'Cancelled'])
    routeFetch({
      'GET /orders/tracking/o-1': () => json(current),
      'POST /orders/tracking/o-1/status': (init) => {
        const body = JSON.parse(init!.body as string)
        current = detail({ status: body.status, cancelReason: body.reason }, [])
        return json(undefined, 204)
      },
    })
    const user = userEvent.setup()
    renderAt('/app/orders/o-1', <OrderTrackingScreen />, '/app/orders/:id')

    expect(await screen.findByRole('heading', { name: 'Pedido P01-W-12' })).toBeInTheDocument()
    expect(screen.getByText('30712345671')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Imprimir remito' })).toHaveAttribute('href', '/print/remitos?orders=o-1')

    await user.click(screen.getByRole('button', { name: 'Cancelar pedido' }))
    const dialog = screen.getByRole('dialog')
    await user.type(within(dialog).getByLabelText('Motivo'), 'El cliente lo anuló')
    await user.click(within(dialog).getByRole('button', { name: 'Cancelar pedido' }))

    await waitFor(() => expect(callsTo('POST /orders/tracking/o-1/status')).toHaveLength(1))
    expect(JSON.parse(callsTo('POST /orders/tracking/o-1/status')[0][1].body as string)).toEqual({
      status: 'Cancelled',
      reason: 'El cliente lo anuló',
    })
    expect(await screen.findByText('El cliente lo anuló')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Imprimir remito' })).not.toBeInTheDocument()
  })

  it('creates a run with the preselected orders in delivery order', async () => {
    routeFetch({
      'GET /orders/tracking': () =>
        json([order(), order({ orderId: 'o-2', orderNumber: 'P01-W-13', customerName: 'Almacén Sur', total: 1000 })]),
      'POST /deliveries/runs': () => json({ runId: 'r-1' }, 201),
    })
    const user = userEvent.setup()
    renderAt('/app/deliveries/new?orders=o-2', <DeliveryRunFormScreen />, '/app/deliveries/new')

    const stops = await screen.findByRole('list', { name: 'Pedidos del reparto' })
    await waitFor(() => expect(within(stops).getByText('P01-W-13')).toBeInTheDocument())
    await user.click(screen.getByRole('button', { name: 'Agregar P01-W-12' }))
    await user.click(screen.getByRole('button', { name: 'Subir P01-W-12' }))
    await user.type(screen.getByLabelText('Chofer'), 'Juan')
    await user.click(screen.getByRole('button', { name: 'Guardar reparto' }))

    await waitFor(() => expect(callsTo('POST /deliveries/runs')).toHaveLength(1))
    expect(JSON.parse(callsTo('POST /deliveries/runs')[0][1].body as string)).toMatchObject({
      driverName: 'Juan',
      vehicle: null,
      orderIds: ['o-1', 'o-2'],
    })
    expect(screen.getByTestId('location')).toHaveTextContent('/app/deliveries/r-1')
  })

  it('offers again an order taken out of the run being edited', async () => {
    const inRun = order({ runId: 'r-1', runNumber: 3 })
    routeFetch({
      'GET /orders/tracking': () => json([inRun, order({ orderId: 'o-9', orderNumber: 'P01-W-99', runId: 'r-7', runNumber: 7 })]),
      'GET /deliveries/runs/r-1': () => json({ run: { runId: 'r-1', runNumber: 3, runDate: '2026-10-05', driverName: 'Juan', vehicle: null, notes: null, status: 'Planned', orderCount: 1, total: 26500, createdAtUtc: '2026-10-05T10:00:00Z', dispatchedAtUtc: null, completedAtUtc: null }, stops: [{ stopNo: 1, order: inRun }] }),
      'PUT /deliveries/runs/r-1': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderAt('/app/deliveries/r-1/edit', <DeliveryRunFormScreen />, '/app/deliveries/:id/edit')

    await user.click(await screen.findByRole('button', { name: 'Quitar P01-W-12' }))

    const available = screen.getByRole('list', { name: 'Pedidos disponibles' })
    expect(within(available).getByText('P01-W-12')).toBeInTheDocument()
    expect(within(available).queryByText('P01-W-99')).not.toBeInTheDocument() // in another run
  })

  it('discards a run still planned, after confirming', async () => {
    routeFetch({
      'GET /deliveries/runs/r-1': () => json({ run: { runId: 'r-1', runNumber: 3, runDate: '2026-10-05', driverName: 'Juan', vehicle: null, notes: null, status: 'Planned', orderCount: 1, total: 26500, createdAtUtc: '2026-10-05T10:00:00Z', dispatchedAtUtc: null, completedAtUtc: null }, stops: [{ stopNo: 1, order: order({ runId: 'r-1' }) }] }),
      'DELETE /deliveries/runs/r-1': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderAt('/app/deliveries/r-1', <DeliveryRunScreen />, '/app/deliveries/:id')

    await user.click(await screen.findByRole('button', { name: 'Eliminar reparto' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveTextContent('Su pedido queda libre para armar otro reparto.')
    await user.click(within(dialog).getByRole('button', { name: 'Eliminar reparto' }))

    await waitFor(() => expect(callsTo('DELETE /deliveries/runs/r-1')).toHaveLength(1))
    expect(screen.getByTestId('location')).toHaveTextContent('/app/deliveries')
  })

  it('settles a run with the re-weighed kilos, the settlement, and an undelivered order', async () => {
    const run: DeliveryRunDetail = {
      run: { runId: 'r-1', runNumber: 3, runDate: '2026-10-05', driverName: 'Juan', vehicle: 'Kangoo', notes: null, status: 'OutForDelivery', orderCount: 2, total: 36500, createdAtUtc: '2026-10-05T10:00:00Z', dispatchedAtUtc: '2026-10-05T11:00:00Z', completedAtUtc: null },
      stops: [
        { stopNo: 1, order: order({ status: 'OutForDelivery', runId: 'r-1', runNumber: 3 }) },
        { stopNo: 2, order: order({ orderId: 'o-2', orderNumber: 'P01-W-13', customerId: null, origin: 'Guest', customerName: 'Invitado', status: 'OutForDelivery', runId: 'r-1', runNumber: 3 }) },
      ],
    }
    routeFetch({
      'GET /deliveries/runs/r-1': () => json(run),
      'GET /orders/tracking/o-1': () => json(detail({ status: 'OutForDelivery' })),
      'GET /orders/tracking/o-2': () => json(detail({ orderId: 'o-2', orderNumber: 'P01-W-13', customerId: null, origin: 'Guest', customerName: 'Invitado', status: 'OutForDelivery' })),
      'POST /deliveries/runs/r-1/settle': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderAt('/app/deliveries/r-1/settle', <DeliveryRunSettleScreen />, '/app/deliveries/:id/settle')

    const kilos = await screen.findByLabelText('Entregado Vacío P01-W-12')
    await user.clear(kilos)
    await user.type(kilos, '2,4')
    const guest = screen.getByRole('group', { name: /P01-W-13/ })
    expect(within(guest).getByLabelText('Liquidación P01-W-13')).toBeDisabled()
    await user.click(within(guest).getByLabelText(/No entregado/))
    expect(screen.getAllByText('Total entregado: $ 25.500,00').length).toBeGreaterThan(0)
    await user.click(screen.getByRole('button', { name: 'Confirmar rendición' }))

    await waitFor(() => expect(callsTo('POST /deliveries/runs/r-1/settle')).toHaveLength(1))
    expect(JSON.parse(callsTo('POST /deliveries/runs/r-1/settle')[0][1].body as string)).toEqual({
      orders: [
        { orderId: 'o-1', delivered: true, settlement: 'CurrentAccount', lines: [{ lineNo: 1, deliveredQuantity: 2.4 }, { lineNo: 2, deliveredQuantity: 3 }] },
        { orderId: 'o-2', delivered: false, settlement: null, lines: null },
      ],
    })
  })

  it('prints every remito twice, ORIGINAL and DUPLICADO, with the issuer and customer data', async () => {
    const remito: RemitoDocument = {
      orderId: 'o-1',
      remitoNumber: 'R01-00000042',
      orderNumber: 'P01-W-12',
      issuedOn: '2026-10-05',
      organization: { name: 'Vaca Verde', legalName: 'Distribuidora Arrecifes S.R.L.', taxId: '30712345671', taxCondition: 'ResponsableInscripto', grossIncomeNumber: null, activityStartDate: null, fiscalAddress: null, documentFooter: null, logoUrl: null, primaryColor: '#2f7d32' },
      branch: { branchId: 'b-1', name: 'Ruta 51', code: 1, address: 'Frascheri 626', locality: 'Arrecifes', phone: '2478-123456', email: null, warehouseAddress: 'Depósito Ruta 51' },
      customer: detail().party,
      lines: detail().lines,
      total: 26500,
      runNumber: 3,
      driverName: 'Juan',
      vehicle: 'Kangoo',
      note: null,
    }
    routeFetch({ 'POST /orders/tracking/remitos': () => json([remito]) })
    renderAt('/print/remitos?orders=o-1', <RemitoPrintScreen />, '/print/remitos')

    const original = await screen.findByRole('article', { name: 'Remito R01-00000042 ORIGINAL' })
    const duplicate = screen.getByRole('article', { name: 'Remito R01-00000042 DUPLICADO' })
    expect(JSON.parse(callsTo('POST /orders/tracking/remitos')[0][1].body as string)).toEqual({ orderIds: ['o-1'] })
    for (const page of [original, duplicate]) {
      expect(within(page).getByText('Distribuidora Arrecifes S.R.L.')).toBeInTheDocument()
      expect(within(page).getByText('Frascheri 626 - Arrecifes')).toBeInTheDocument()
      expect(within(page).getByText(/Depósito Ruta 51/)).toBeInTheDocument()
      expect(within(page).getByText('CUIT: 30-71234567-1')).toBeInTheDocument()
      expect(within(page).getByText('$ 26.500,00')).toBeInTheDocument()
    }
    expect(within(duplicate).getByText('Copia para Vaca Verde: firmar al recibir')).toBeInTheDocument()
    await waitFor(() => expect(window.print).toHaveBeenCalledTimes(1))
  })

  it('saves the organization document data', async () => {
    routeFetch({
      'GET /account/organization/document-profile': () =>
        json({ name: 'Vaca Verde', legalName: null, taxId: null, taxCondition: null, grossIncomeNumber: null, activityStartDate: null, fiscalAddress: null, documentFooter: null, logoUrl: null, primaryColor: null }),
      'PUT /account/organization/document-profile': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    render(<OrganizationDocumentsForm />)

    await user.type(await screen.findByLabelText('Razón social'), 'Distribuidora Arrecifes S.R.L.')
    await user.type(screen.getByLabelText('CUIT'), '30712345671')
    await user.selectOptions(screen.getByLabelText('Condición frente al IVA'), 'ResponsableInscripto')
    await user.click(screen.getByRole('button', { name: 'Guardar datos' }))

    expect(await screen.findByText('Datos guardados.')).toBeInTheDocument()
    expect(JSON.parse(callsTo('PUT /account/organization/document-profile')[0][1].body as string)).toMatchObject({
      legalName: 'Distribuidora Arrecifes S.R.L.',
      taxId: '30712345671',
      taxCondition: 'ResponsableInscripto',
    })
  })
})
