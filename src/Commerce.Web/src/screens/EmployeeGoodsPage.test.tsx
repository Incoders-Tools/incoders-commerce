import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeRecord, GoodsItem } from '@/api/employees'
import { todayIso } from '@/lib/isoDate'
import { EmployeeGoodsPage } from './EmployeeGoodsPage'

/**
 * employee-lifecycle E4c: the goods an employee takes, registered in the web like a sale at the counter (Mostrador)
 * price: products and quantities, the server's prices and total, and one registration per draft even when retried.
 */
describe('EmployeeGoodsPage', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const juan = {
    id: 'e-1', branchId: 'b-1', branchName: 'Arrecifes', fileNumber: 1, firstName: 'Juan', lastName: 'Pérez', fullName: 'Pérez, Juan',
    documentNumber: null, cuil: null, roleId: null, roleName: null, phone: null, email: null, address: null, hireDate: null,
    terminationDate: null, payFrequency: 'Monthly', baseSalary: 500000, customerId: 'c-1', notes: null, isActive: true,
    createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', balance: 0, purchasesOwed: 0, overtimeHourlyRate: null,
    terminationReason: null,
  } as EmployeeRecord

  const asado = {
    presentationId: 'p-1', productId: 'pr-1', productName: 'Asado', presentationName: 'Por kg', identificationCode: null,
    quantityBehavior: 'Weighted',
  }

  const item = (overrides: Partial<GoodsItem> = {}): GoodsItem => ({
    id: 'g-1', sourceType: 'Web', sourceId: 's-1', lineNo: 1, occurredOn: '2026-10-12', presentationId: 'p-1', productName: 'Asado',
    presentationName: 'Por kg', quantity: 1.5, unitPrice: 10000, lineTotal: 15000, discountPercent: 0, status: 'Pending', runNumber: null,
    ...overrides,
  })

  const quoteLine = (quantity: number) => ({
    presentationId: 'p-1', productName: 'Asado', presentationName: 'Por kg', quantity, unitPrice: 10000, lineTotal: 10000 * quantity,
  })

  let stored: GoodsItem[]
  let register: () => Response

  beforeEach(() => {
    stored = []
    register = () => {
      stored = [item()]
      return json({ goodsId: 'k', total: 15000, lines: [quoteLine(1.5)] }, 201)
    }
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /employees/e-1/goods') return json(stored)
      if (key.startsWith('GET /orders/staff/presentations')) return json([asado])
      if (key === 'POST /employees/e-1/goods/quote') {
        const quantity = JSON.parse(init!.body as string).lines[0].quantity as number
        return json({ lines: [quoteLine(quantity)], total: 10000 * quantity })
      }
      if (key === 'POST /employees/e-1/goods') return register()
      throw new TypeError(`unrouted ${key}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const calls = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${url}` === key)
  const bodies = (key: string) => calls(key).map(([, init]) => JSON.parse(init.body as string))

  async function addAsado(user: ReturnType<typeof userEvent.setup>, quantity: string) {
    await user.type(screen.getByLabelText('Buscar producto'), 'asa')
    await user.click(await screen.findByRole('button', { name: 'Agregar Asado Por kg' }))
    await user.type(screen.getByLabelText('Cantidad de Asado Por kg'), quantity)
  }

  it('registers products and quantities at the server prices, and lists them as pending', async () => {
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    expect(await screen.findByText('No tiene mercadería registrada.')).toBeInTheDocument()

    await addAsado(user, '1,5')
    expect(await screen.findByText(/Total: \$\s15\.000,00/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))

    await waitFor(() => expect(calls('POST /employees/e-1/goods')).toHaveLength(1))
    const [sent] = bodies('POST /employees/e-1/goods')
    expect(sent).toMatchObject({ occurredOn: todayIso(), lines: [{ presentationId: 'p-1', quantity: 1.5 }] })
    expect(typeof sent.goodsId).toBe('string')
    expect(await screen.findByText(/Mercadería registrada por \$\s15\.000,00/)).toBeInTheDocument()
    const row = screen.getByText('Pendiente').closest('tr')!
    expect(within(row).getByText('Asado')).toBeInTheDocument()
    expect(within(row).getByText(/15\.000,00/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Cantidad de Asado Por kg')).not.toBeInTheDocument()
  })

  it('retries a registration whose answer was lost with the same key, so it is registered once', async () => {
    let attempts = 0
    register = () => {
      attempts += 1
      if (attempts === 1) throw new TypeError('network down')
      stored = [item()]
      return json({ goodsId: 'k', total: 15000, lines: [quoteLine(1.5)] }, 201)
    }
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    await addAsado(user, '1,5')
    await screen.findByText(/Total: \$\s15\.000,00/)
    await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))

    await waitFor(() => expect(calls('POST /employees/e-1/goods')).toHaveLength(2))
    const [first, second] = bodies('POST /employees/e-1/goods')
    expect(second.goodsId).toBe(first.goodsId)
  })

  it('keeps the key when the draft is edited after a lost answer, so the first registration is never doubled', async () => {
    let attempts = 0
    register = () => {
      attempts += 1
      if (attempts === 1) throw new TypeError('network down')
      stored = [item()]
      return json({ goodsId: 'k', total: 15000, lines: [quoteLine(1.5)] }, 201)
    }
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    await addAsado(user, '1,5')
    await screen.findByText(/Total: \$\s15\.000,00/)
    await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Cantidad de Asado Por kg'), '5')
    await screen.findByText(/Total: \$\s15\.500,00/)
    await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))

    await waitFor(() => expect(calls('POST /employees/e-1/goods')).toHaveLength(2))
    const [first, second] = bodies('POST /employees/e-1/goods')
    expect(second.goodsId).toBe(first.goodsId)
  })

  it('gives the next registration a new key once one succeeded', async () => {
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    for (const round of [1, 2]) {
      await addAsado(user, '1,5')
      await screen.findByText(/Total: \$\s15\.000,00/)
      await user.click(screen.getByRole('button', { name: 'Registrar mercadería' }))
      await waitFor(() => expect(calls('POST /employees/e-1/goods')).toHaveLength(round))
      await screen.findByText(/Mercadería registrada por/)
    }
    const [first, second] = bodies('POST /employees/e-1/goods')
    expect(second.goodsId).not.toBe(first.goodsId)
  })

  it('says in what unit each quantity goes: kilos for a weighed product, units for one sold by the unit', async () => {
    const pack = { ...asado, presentationId: 'p-2', productName: 'Chorizo', presentationName: 'Paquete', quantityBehavior: 'FixedQuantity' }
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /employees/e-1/goods') return json([])
      if (key.startsWith('GET /orders/staff/presentations')) return json([asado, pack])
      if (key === 'POST /employees/e-1/goods/quote') return json({ lines: [], total: 0 })
      throw new TypeError(`unrouted ${key}`)
    })
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    await user.type(screen.getByLabelText('Buscar producto'), 'a')
    await user.click(await screen.findByRole('button', { name: 'Agregar Asado Por kg' }))
    await user.type(screen.getByLabelText('Buscar producto'), 'c')
    await user.click(await screen.findByRole('button', { name: 'Agregar Chorizo Paquete' }))

    const kilos = screen.getByLabelText('Cantidad de Asado Por kg')
    expect(kilos).toHaveAttribute('placeholder', 'Kilos (Ej: 1,5)')
    expect(within(kilos.closest('li')!).getByText('kg')).toBeInTheDocument()
    const units = screen.getByLabelText('Cantidad de Chorizo Paquete')
    expect(units).toHaveAttribute('placeholder', 'Unidades (Ej: 2)')
    expect(units).toHaveAttribute('inputmode', 'numeric')
    expect(within(units.closest('li')!).getByText('u.')).toBeInTheDocument()
  })

  it('offers to price again when pricing failed (a dropped connection), without retyping', async () => {
    let quotes = 0
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/employees/e-1/goods/quote' && ++quotes === 1) throw new TypeError('network down')
      return original(url, init)
    })
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    await addAsado(user, '1,5')
    expect(await screen.findByText('No se pudieron calcular los precios. Probá de nuevo.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar mercadería' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText(/Total: \$\s15\.000,00/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar mercadería' })).toBeEnabled()
  })

  it('explains a product without a counter price and does not let it be registered', async () => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /employees/e-1/goods') return json([])
      if (key.startsWith('GET /orders/staff/presentations')) return json([asado])
      if (key === 'POST /employees/e-1/goods/quote') return json({ error: 'no-effective-price' }, 409)
      throw new TypeError(`unrouted ${key}`)
    })
    const user = userEvent.setup()
    render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)
    await screen.findByText('No tiene mercadería registrada.')

    await addAsado(user, '1')
    expect(await screen.findByText(/no tiene precio vigente en la lista Mostrador/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Reintentar' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Registrar mercadería' })).toBeDisabled()
  })

  // owner feedback round 2, R2: a registration made by mistake is voided whole, or corrected (voided and loaded again).
  describe('voiding and correcting a registration', () => {
    const chorizo = (overrides: Partial<GoodsItem> = {}) =>
      item({ id: 'g-2', lineNo: 2, presentationId: 'p-2', productName: 'Chorizo', quantity: 2, unitPrice: 4500, lineTotal: 9000,
        productId: 'pr-2', quantityBehavior: 'FixedQuantity', ...overrides })

    function route(onVoid: () => Response) {
      fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
        const key = `${init?.method ?? 'GET'} ${url}`
        if (key === 'GET /employees/e-1/goods') return json(stored)
        if (key === 'POST /employees/e-1/goods/s-1/void') return onVoid()
        if (key === 'POST /employees/e-1/goods/quote') {
          const lines = JSON.parse(init!.body as string).lines as { presentationId: string; quantity: number }[]
          return json({ lines: [], total: lines.length })
        }
        throw new TypeError(`unrouted ${key}`)
      })
    }

    it('voids a whole registration after confirming, and lists what is left', async () => {
      stored = [item({ productId: 'pr-1', quantityBehavior: 'Weighted' }), chorizo(), item({ id: 'g-3', sourceId: 's-2', productName: 'Vacío' })]
      route(() => {
        stored = stored.filter((other) => other.sourceId !== 's-1')
        return json(undefined, 204)
      })
      const user = userEvent.setup()
      render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)

      // One pair of actions per registration (its two items share it).
      await user.click(await screen.findByRole('button', { name: 'Anular la carga del 12/10/2026 (Asado, Chorizo)' }))
      const dialog = screen.getByRole('dialog')
      expect(within(dialog).getByText(/\$\s24\.000,00/)).toBeInTheDocument()
      expect(calls('POST /employees/e-1/goods/s-1/void')).toHaveLength(0)
      await user.click(within(dialog).getByRole('button', { name: 'Anular carga' }))

      await waitFor(() => expect(calls('POST /employees/e-1/goods/s-1/void')).toHaveLength(1))
      expect(await screen.findByText('Carga anulada.')).toBeInTheDocument()
      await waitFor(() => expect(screen.queryByText('Chorizo')).not.toBeInTheDocument())
      expect(screen.getByText('Vacío')).toBeInTheDocument()
    })

    it('corrects a registration: voids it and loads its products and date again', async () => {
      stored = [item({ occurredOn: '2026-10-10', productId: 'pr-1', quantityBehavior: 'Weighted' }), chorizo({ occurredOn: '2026-10-10' })]
      route(() => {
        stored = []
        return json(undefined, 204)
      })
      const user = userEvent.setup()
      render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)

      await user.click(await screen.findByRole('button', { name: 'Corregir la carga del 10/10/2026 (Asado, Chorizo)' }))
      await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Anular y corregir' }))

      await waitFor(() => expect(calls('POST /employees/e-1/goods/s-1/void')).toHaveLength(1))
      expect(await screen.findByText(/corregí los productos y registralos de nuevo/)).toBeInTheDocument()
      expect(screen.getByLabelText('Cantidad de Asado Por kg')).toHaveValue('1,5')
      expect(screen.getByLabelText('Cantidad de Chorizo Por kg')).toHaveValue('2')
      expect(screen.getByLabelText('Cantidad de Chorizo Por kg')).toHaveAttribute('placeholder', 'Unidades (Ej: 2)')
      expect(screen.getByLabelText('Fecha')).toHaveValue('10/10/2026')
    })

    it('explains why goods a paid payroll settled cannot be voided, and offers no action for them nor for a POS sale', async () => {
      stored = [item({ status: 'InPayroll', runNumber: 4 }), item({ id: 'g-9', sourceId: 's-9', sourceType: 'PosSale' }),
        item({ id: 'g-8', sourceId: 's-8', status: 'Paid', runNumber: 3 })]
      route(() => json({ error: 'goods-settled' }, 409))
      const user = userEvent.setup()
      render(<EmployeeGoodsPage employee={juan} onBack={vi.fn()} />)

      expect(await screen.findAllByRole('button', { name: /^Anular la carga/ })).toHaveLength(1)
      await user.click(screen.getByRole('button', { name: /^Anular la carga/ }))
      await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Anular carga' }))
      expect(await screen.findByText(/una liquidación pagada ya la descontó/)).toBeInTheDocument()
    })
  })

  it('tells an employee who takes no goods how to enable them, and still lists what was taken', async () => {
    stored = [item({ sourceType: 'PosSale', status: 'Paid', runNumber: 3 })]
    render(<EmployeeGoodsPage employee={{ ...juan, customerId: null }} onBack={vi.fn()} />)

    expect(await screen.findByText(/activá «Lleva mercadería»/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Buscar producto')).not.toBeInTheDocument()
    const row = (await screen.findByText('Descontada en la liquidación 3')).closest('tr')!
    expect(within(row).getByText('Venta POS')).toBeInTheDocument()
  })
})
