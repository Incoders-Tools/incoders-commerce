import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import type { StaffCustomerOption, StaffOrderQuote, StaffPresentationOption } from '@/api/types'
import { StaffOrderScreen } from './StaffOrderScreen'
import { json } from './supplierFixtures'

const customer = (overrides: Partial<StaffCustomerOption> = {}): StaffCustomerOption => ({
  id: 'c-1',
  displayName: 'Almacén Ramírez',
  taxId: '20-12345678-9',
  phone: '3794 112233',
  cityName: 'Corrientes',
  priceListId: 'pl-reparto',
  priceListName: 'Reparto',
  discountPercentage: 5,
  isEnabled: true,
  ...overrides,
})

const presentation = (overrides: Partial<StaffPresentationOption> = {}): StaffPresentationOption => ({
  presentationId: 'pr-1',
  productId: 'p-1',
  productName: 'Chorizo',
  presentationName: 'Paquete',
  identificationCode: '779001',
  quantityBehavior: 'FixedQuantity',
  ...overrides,
})

const presentations = [
  presentation(),
  presentation({
    presentationId: 'pr-2',
    productId: 'p-2',
    productName: 'Vacío',
    presentationName: 'Kilo',
    identificationCode: null,
    quantityBehavior: 'Weighted',
  }),
]

type QuoteLineInput = { productId: string; presentationId: string; quantity: number }

/** Prices every line at 100 (net 95 after the 5% discount); `pr-2` falls back to the default list. */
function quoteFor(body: { customerId: string; lines: QuoteLineInput[] }, unpriced: string[] = []): StaffOrderQuote {
  const lines = body.lines.map((line) => {
    const option = presentations.find((p) => p.presentationId === line.presentationId)!
    if (unpriced.includes(line.presentationId)) {
      return {
        ...line,
        status: 'no-effective-price' as const,
        productName: null,
        presentationName: null,
        quantityBehavior: null,
        unitListPrice: null,
        appliedDiscountPercentage: null,
        unitNetPrice: null,
        lineTotal: null,
        priceListId: null,
        priceListName: null,
        fellBack: false,
      }
    }
    const fellBack = line.presentationId === 'pr-2'
    return {
      ...line,
      status: 'priced' as const,
      productName: option.productName,
      presentationName: option.presentationName,
      quantityBehavior: option.quantityBehavior,
      unitListPrice: 100,
      appliedDiscountPercentage: 5,
      unitNetPrice: 95,
      lineTotal: 95 * line.quantity,
      priceListId: fellBack ? 'pl-mostrador' : 'pl-reparto',
      priceListName: fellBack ? 'Mostrador' : 'Reparto',
      fellBack,
    }
  })
  const allPriced = lines.every((line) => line.status === 'priced')
  return {
    status: allPriced ? 'quoted' : 'denied',
    reason: allPriced ? 'quoted' : 'no-effective-price',
    customerId: body.customerId,
    priceListId: 'pl-reparto',
    priceListName: 'Reparto',
    discountPercentage: 5,
    lines,
    total: lines.reduce((sum, line) => sum + (line.lineTotal ?? 0), 0),
  }
}

describe('StaffOrderScreen', () => {
  const fetchMock = vi.fn()
  let customers: StaffCustomerOption[]
  let unpriced: string[]
  let submitResponse: (body: Record<string, unknown>) => Response | Promise<Response>

  const calls = (method: string, path: string) =>
    fetchMock.mock.calls.filter(
      ([url, init]) => (init?.method ?? 'GET') === method && String(url).split('?')[0] === path,
    )
  const lastBody = (method: string, path: string) => {
    const matching = calls(method, path)
    return JSON.parse(matching[matching.length - 1][1].body as string)
  }

  beforeEach(() => {
    customers = [customer(), customer({ id: 'c-2', displayName: 'Ramos Hnos', cityName: null, priceListName: null, priceListId: null, isEnabled: false })]
    unpriced = []
    submitResponse = (body) =>
      json({
        status: 'accepted',
        reason: 'accepted',
        wasNewlyAccepted: true,
        orderNumber: 'P01-W-1',
        order: { orderId: body.orderId },
      })
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const path = url.split('?')[0]
      if (path === '/orders/staff/customers') return json(customers)
      if (path === '/orders/staff/presentations') {
        const search = new URL(url, 'http://test').searchParams.get('search')?.toLowerCase() ?? ''
        return json(presentations.filter((p) => p.productName.toLowerCase().includes(search)))
      }
      if (path === '/orders/staff/quote') {
        const quote = quoteFor(JSON.parse(init!.body as string), unpriced)
        return json(quote, quote.status === 'quoted' ? 200 : 422)
      }
      if (path === '/orders/staff' || path === '/orders/staff/') return submitResponse(JSON.parse(init!.body as string))
      throw new Error(`unexpected ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const submitButton = () => screen.getByRole('button', { name: 'Confirmar pedido' })

  async function pickCustomer(user: ReturnType<typeof userEvent.setup>, name = 'Almacén Ramírez') {
    await user.type(screen.getByLabelText('Buscar cliente'), 'ram')
    await user.click(await screen.findByRole('button', { name: new RegExp(name) }))
  }

  async function addProduct(user: ReturnType<typeof userEvent.setup>, productName: string) {
    const search = screen.getByLabelText('Buscar producto')
    await user.clear(search)
    await user.type(search, productName.slice(0, 3))
    await user.click(await screen.findByRole('button', { name: new RegExp(`Agregar ${productName}`) }))
  }

  it('searches customers on the server and shows city, price list and why a customer cannot be chosen', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    expect(screen.getByRole('heading', { name: 'Tomar pedido' })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Buscar cliente'), 'ram')

    const enabled = await screen.findByRole('button', { name: /Almacén Ramírez/ })
    expect(enabled).toHaveTextContent('Corrientes')
    expect(enabled).toHaveTextContent('Reparto')
    const disabled = screen.getByRole('button', { name: /Ramos Hnos/ })
    expect(disabled).toBeDisabled()
    expect(disabled).toHaveTextContent('Cliente deshabilitado')
    const searched = calls('GET', '/orders/staff/customers').map(([url]) => String(url))
    expect(searched[searched.length - 1]).toBe('/orders/staff/customers?search=ram')

    await user.click(enabled)
    const selected = screen.getByRole('region', { name: 'Cliente' })
    expect(within(selected).getByText('Almacén Ramírez')).toBeInTheDocument()
    expect(within(selected).getByText(/Reparto/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Buscar cliente')).not.toBeInTheDocument()

    await user.click(within(selected).getByRole('button', { name: 'Cambiar' }))
    expect(screen.getByLabelText('Buscar cliente')).toBeInTheDocument()
  })

  it('adds products as lines, merges the same presentation and removes a line', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await addProduct(user, 'Chorizo')
    await addProduct(user, 'Chorizo')
    expect(screen.getByLabelText('Cantidad de Chorizo Paquete')).toHaveValue('2')
    expect(screen.getAllByRole('listitem', { name: /Chorizo Paquete/ })).toHaveLength(1)

    await addProduct(user, 'Vacío')
    expect(screen.getByLabelText('Cantidad de Vacío Kilo')).toHaveValue('1')

    await user.click(screen.getByRole('button', { name: 'Quitar Chorizo Paquete' }))
    expect(screen.queryByLabelText('Cantidad de Chorizo Paquete')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Cantidad de Vacío Kilo')).toBeInTheDocument()
  })

  it('quotes the draft and shows per line price, the list used, the fallback note and the total', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await addProduct(user, 'Vacío')
    await user.clear(screen.getByLabelText('Cantidad de Vacío Kilo'))
    await user.type(screen.getByLabelText('Cantidad de Vacío Kilo'), '1,5')

    await waitFor(() =>
      expect(lastBody('POST', '/orders/staff/quote')).toEqual({
        customerId: 'c-1',
        lines: [
          { productId: 'p-1', presentationId: 'pr-1', quantity: 1 },
          { productId: 'p-2', presentationId: 'pr-2', quantity: 1.5 },
        ],
      }),
    )

    const chorizo = screen.getByRole('listitem', { name: /Chorizo Paquete/ })
    expect(await within(chorizo).findByText('$ 95,00')).toBeInTheDocument()
    expect(within(chorizo).getByText(/Lista Reparto/)).toBeInTheDocument()
    const vacio = screen.getByRole('listitem', { name: /Vacío Kilo/ })
    expect(await within(vacio).findByText('$ 142,50')).toBeInTheDocument()
    expect(within(vacio).getByText(/Sin precio en la lista del cliente: se usó Mostrador/)).toBeInTheDocument()
    expect(screen.getByTestId('order-total')).toHaveTextContent('$ 237,50')
    await waitFor(() => expect(submitButton()).toBeEnabled())
  })

  it('accepts only whole units for fixed-quantity products and blocks submit while a quantity is invalid', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await user.clear(screen.getByLabelText('Cantidad de Chorizo Paquete'))
    await user.type(screen.getByLabelText('Cantidad de Chorizo Paquete'), '1,5')

    expect(await screen.findByText('Ingresá una cantidad entera mayor a cero.')).toBeInTheDocument()
    expect(submitButton()).toBeDisabled()
  })

  it('marks lines without a price and blocks submit', async () => {
    unpriced = ['pr-2']
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await addProduct(user, 'Vacío')

    const vacio = screen.getByRole('listitem', { name: /Vacío Kilo/ })
    expect(await within(vacio).findByText('Sin precio para este cliente')).toBeInTheDocument()
    expect(screen.getByText('Quitá los productos sin precio para poder confirmar el pedido.')).toBeInTheDocument()
    expect(submitButton()).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Quitar Vacío Kilo' }))
    await waitFor(() => expect(submitButton()).toBeEnabled())
  })

  it('submits the order and shows its number, then starts a new draft', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await user.type(screen.getByLabelText('Nota (opcional)'), 'Entregar a la mañana')
    expect(screen.getByText('20/500')).toBeInTheDocument()
    await waitFor(() => expect(submitButton()).toBeEnabled())
    await user.click(submitButton())

    expect(await screen.findByRole('heading', { name: /Pedido P01-W-1 registrado/ })).toBeInTheDocument()
    const body = lastBody('POST', '/orders/staff')
    expect(body).toMatchObject({
      customerId: 'c-1',
      lines: [{ productId: 'p-1', presentationId: 'pr-1', quantity: 1 }],
      note: 'Entregar a la mañana',
    })
    expect(body.orderId).toMatch(/^[0-9a-f-]{36}$/)
    expect(screen.queryByText(body.orderId)).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Nuevo pedido' }))
    expect(screen.getByLabelText('Buscar cliente')).toHaveValue('')
    expect(screen.queryByRole('listitem')).not.toBeInTheDocument()
  })

  it('keeps the same order id when the submit is retried', async () => {
    let attempts = 0
    submitResponse = (body) => {
      attempts += 1
      if (attempts === 1) throw new TypeError('network down')
      return json({ status: 'accepted', reason: 'existing-order', wasNewlyAccepted: false, orderNumber: 'P01-W-7', order: { orderId: body.orderId } })
    }
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await waitFor(() => expect(submitButton()).toBeEnabled())
    await user.click(submitButton())
    expect(await screen.findByRole('alert')).toBeInTheDocument()

    await user.click(submitButton())
    expect(await screen.findByRole('heading', { name: /Pedido P01-W-7 registrado/ })).toBeInTheDocument()
    const [first, second] = calls('POST', '/orders/staff').map(([, init]) => JSON.parse(init.body as string).orderId)
    expect(second).toBe(first)
  })

  it('never lets a late answer to an older draft replace the current prices', async () => {
    let releaseFirst: (() => void) | null = null
    const baseImplementation = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/orders/staff/quote' && releaseFirst === null && JSON.parse(init!.body as string).lines[0].quantity === 1) {
        const response = await baseImplementation(url, init)
        // The first quote (quantity 1) answers only after the second one.
        await new Promise<void>((resolve) => {
          releaseFirst = resolve
        })
        return response
      }
      return baseImplementation(url, init)
    })
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await waitFor(() => expect(releaseFirst).not.toBeNull())
    await user.clear(screen.getByLabelText('Cantidad de Chorizo Paquete'))
    await user.type(screen.getByLabelText('Cantidad de Chorizo Paquete'), '3')

    await waitFor(() => expect(screen.getByTestId('order-total')).toHaveTextContent('$ 285,00'))
    releaseFirst!()
    await new Promise((resolve) => setTimeout(resolve, 50))
    expect(screen.getByTestId('order-total')).toHaveTextContent('$ 285,00')
  })

  it('uses a new order id for the next draft', async () => {
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    for (let order = 0; order < 2; order++) {
      await pickCustomer(user)
      await addProduct(user, 'Chorizo')
      await waitFor(() => expect(submitButton()).toBeEnabled())
      await user.click(submitButton())
      await user.click(await screen.findByRole('button', { name: 'Nuevo pedido' }))
    }

    const [first, second] = calls('POST', '/orders/staff').map(([, init]) => JSON.parse(init.body as string).orderId)
    expect(second).not.toBe(first)
  })

  it.each([
    [422, 'no-effective-price', 'Algún producto no tiene precio para este cliente.'],
    [409, 'customer-disabled', 'El cliente está deshabilitado: no se le pueden tomar pedidos.'],
    [409, 'order-id-conflict', 'No se pudo registrar el pedido (order-id-conflict).'],
    [404, 'not-found', 'El cliente o algún producto ya no existe.'],
  ])('maps a %i %s answer to a clear message', async (status, reason, message) => {
    submitResponse = () => json({ status: 'denied', reason, wasNewlyAccepted: false, orderNumber: null, order: null }, status)
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await waitFor(() => expect(submitButton()).toBeEnabled())
    await user.click(submitButton())

    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(screen.queryByRole('heading', { name: /registrado/ })).not.toBeInTheDocument()
  })

  it('maps a validation problem to a clear message', async () => {
    submitResponse = () => json({ title: 'One or more validation errors occurred.', errors: { note: ['too long'] } }, 400)
    const user = userEvent.setup()
    render(<StaffOrderScreen />)

    await pickCustomer(user)
    await addProduct(user, 'Chorizo')
    await waitFor(() => expect(submitButton()).toBeEnabled())
    await user.click(submitButton())

    expect(await screen.findByRole('alert')).toHaveTextContent('Revisá los datos del pedido.')
  })

  it('asks for a branch instead of calling the API when none is selected', () => {
    render(
      <BranchContext.Provider value={{ selectedBranch: null, selectableBranches: [], selectBranch: () => {} }}>
        <StaffOrderScreen />
      </BranchContext.Provider>,
    )

    expect(screen.getByRole('status')).toHaveTextContent('Elegí una sucursal en la barra superior para tomar pedidos.')
    expect(screen.queryByLabelText('Buscar cliente')).not.toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
