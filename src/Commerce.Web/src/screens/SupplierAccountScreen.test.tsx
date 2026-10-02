import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SupplierAccountScreen } from './SupplierAccountScreen'
import type { AccountSummary, MovementDirection, StatementLine } from '@/api/types'
import { json, supplierFixture } from './supplierFixtures'

const SUPPLIER = supplierFixture({ paymentTermsDays: 30 })

const pad = (n: number) => String(n).padStart(2, '0')
const isoDate = (date: Date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
const today = () => isoDate(new Date())
const daysAgo = (days: number) => {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return isoDate(date)
}

const DIRECTION_BY_KIND: Record<string, MovementDirection> = {
  OpeningBalance: 'Credit',
  Invoice: 'Credit',
  DebitNote: 'Credit',
  CreditNote: 'Debit',
  Payment: 'Debit',
}

/**
 * A tiny in-memory ledger standing behind the mocked fetch, so the screen is
 * exercised end to end: register, reverse, statement and summary all derive
 * from the same movements, like the real API does.
 */
function createFakeLedger() {
  let sequence = 0
  const lines: StatementLine[] = []
  const signed = (m: { direction: MovementDirection; amount: number }) => (m.direction === 'Credit' ? m.amount : -m.amount)
  const append = (movement: Omit<StatementLine, 'id' | 'runningBalance' | 'reversed' | 'reversedByMovementId' | 'supplierId' | 'createdAtUtc' | 'createdByUserId'>) => {
    const line: StatementLine = {
      id: `m${++sequence}`,
      supplierId: SUPPLIER.id,
      createdAtUtc: '2026-10-02T00:00:00Z',
      createdByUserId: 'u1',
      runningBalance: 0,
      reversed: false,
      reversedByMovementId: null,
      ...movement,
    }
    lines.push(line)
    return line
  }
  return {
    lines,
    balance: () => lines.reduce((sum, line) => sum + signed(line), 0),
    register(body: Record<string, unknown>) {
      const direction = (body.direction as MovementDirection | undefined) ?? DIRECTION_BY_KIND[body.kind as string]
      return append({
        kind: body.kind as StatementLine['kind'],
        direction,
        amount: body.amount as number,
        occurredOn: (body.occurredOn as string | undefined) ?? today(),
        dueOn: (body.dueOn as string | undefined) ?? null,
        documentReference: (body.documentReference as string | undefined) ?? null,
        concept: body.concept as string,
        reversesMovementId: null,
      })
    },
    reverse(id: string, body: Record<string, unknown>) {
      const original = lines.find((line) => line.id === id)!
      const reversal = append({
        kind: original.kind,
        direction: original.direction === 'Credit' ? 'Debit' : 'Credit',
        amount: original.amount,
        occurredOn: (body.occurredOn as string | undefined) ?? today(),
        dueOn: null,
        documentReference: original.documentReference,
        concept: (body.concept as string | undefined) ?? `Anulación de ${original.concept}`,
        reversesMovementId: original.id,
      })
      original.reversed = true
      original.reversedByMovementId = reversal.id
      return reversal
    },
    statement(from: string | null, to: string | null) {
      let running = 0
      const all = lines.map((line) => ({ ...line, runningBalance: (running += signed(line)) }))
      const inRange = all.filter((line) => (!from || line.occurredOn >= from) && (!to || line.occurredOn <= to))
      const opening = all.filter((line) => from && line.occurredOn < from).reduce((sum, line) => sum + signed(line), 0)
      return { openingBalance: opening, movements: inRange, closingBalance: running }
    },
  }
}

describe('SupplierAccountScreen', () => {
  const fetchMock = vi.fn()
  let ledger: ReturnType<typeof createFakeLedger>
  let summaryOverride: Partial<AccountSummary> | null

  const urls = (needle: string) =>
    fetchMock.mock.calls.map((call) => call[0] as string).filter((url) => url.includes(needle))
  const postBodies = () =>
    fetchMock.mock.calls.filter((call) => call[1]?.method === 'POST').map((call) => JSON.parse(call[1].body as string))

  beforeEach(() => {
    ledger = createFakeLedger()
    summaryOverride = null
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const { pathname, searchParams } = new URL(url, 'http://localhost')
      const body = init?.body ? JSON.parse(init.body as string) : {}
      if (pathname === `/suppliers/${SUPPLIER.id}`) return json(SUPPLIER)
      if (pathname.endsWith('/account/summary')) {
        const balance = ledger.balance()
        return json({
          asOf: today(),
          balance,
          overdue: 0,
          current: Math.max(balance, 0),
          aging: { d0_30: 0, d31_60: 0, d61_90: 0, d90plus: 0 },
          ...summaryOverride,
        })
      }
      if (pathname.endsWith('/account/statement')) return json(ledger.statement(searchParams.get('from'), searchParams.get('to')))
      if (pathname.endsWith('/reverse')) return json(ledger.reverse(pathname.split('/').at(-2)!, body), 201)
      if (pathname.endsWith('/account/movements')) return json(ledger.register(body), 201)
      return json({}, 404)
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
      <MemoryRouter initialEntries={[`/app/suppliers/${SUPPLIER.id}/account`]}>
        <Routes>
          <Route path="/app/suppliers/:id/account" element={<SupplierAccountScreen />} />
          <Route path="/app/suppliers" element={<p>LISTA DE PROVEEDORES</p>} />
        </Routes>
      </MemoryRouter>,
    )

  /** Opens the register form, fills it and submits. */
  const register = async (
    user: ReturnType<typeof userEvent.setup>,
    entry: { kind: string; amount: string; concept: string; dueOn?: string; direction?: string },
  ) => {
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), entry.kind)
    if (entry.direction) await user.selectOptions(screen.getByLabelText('Efecto del ajuste'), entry.direction)
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: entry.amount } })
    fireEvent.change(screen.getByLabelText('Concepto'), { target: { value: entry.concept } })
    if (entry.dueOn) fireEvent.change(screen.getByLabelText('Vencimiento'), { target: { value: entry.dueOn } })
    await user.click(screen.getByRole('button', { name: 'Registrar' }))
  }

  it('shows the supplier name, the summary cards and the aging buckets', async () => {
    summaryOverride = {
      balance: 90000,
      overdue: 50000,
      current: 40000,
      aging: { d0_30: 20000, d31_60: 10000, d61_90: 15000, d90plus: 5000 },
    }
    renderScreen()

    expect(await screen.findByRole('heading', { name: 'Cuenta corriente: Frigorífico Norte' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByTestId('summary-balance')).toHaveTextContent(/90\.000,00/))
    expect(screen.getByTestId('summary-balance')).toHaveTextContent('Le debemos')
    expect(screen.getByTestId('summary-overdue')).toHaveTextContent(/50\.000,00/)
    expect(screen.getByTestId('summary-current')).toHaveTextContent(/40\.000,00/)
    expect(screen.getByTestId('aging-d0_30')).toHaveTextContent(/1 a 30 días.*20\.000,00/)
    expect(screen.getByTestId('aging-d31_60')).toHaveTextContent(/10\.000,00/)
    expect(screen.getByTestId('aging-d61_90')).toHaveTextContent(/15\.000,00/)
    expect(screen.getByTestId('aging-d90plus')).toHaveTextContent(/Más de 90 días.*5\.000,00/)
  })

  it('words a negative balance as a balance in our favour', async () => {
    summaryOverride = { balance: -5000, current: 0 }
    renderScreen()

    await waitFor(() => expect(screen.getByTestId('summary-balance')).toHaveTextContent(/5\.000,00/))
    expect(screen.getByTestId('summary-balance')).toHaveTextContent('Saldo a favor')
    expect(screen.getByTestId('summary-balance')).not.toHaveTextContent('Le debemos')
  })

  it('loads the statement for the last 90 days by default and reloads it for a chosen range', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('No hay movimientos en este período.')

    const statementUrl = new URL(urls('/account/statement')[0], 'http://localhost')
    expect(statementUrl.searchParams.get('from')).toBe(daysAgo(90))
    expect(statementUrl.searchParams.get('to')).toBe(today())

    fireEvent.change(screen.getByLabelText('Desde'), { target: { value: '2026-01-01' } })
    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-01-31' } })
    await user.click(screen.getByRole('button', { name: 'Actualizar' }))

    await waitFor(() => expect(urls('/account/statement').at(-1)).toContain('from=2026-01-01&to=2026-01-31'))
  })

  it('does not call the server with an inverted date range', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('No hay movimientos en este período.')
    const before = urls('/account/statement').length

    fireEvent.change(screen.getByLabelText('Desde'), { target: { value: '2026-02-01' } })
    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-01-01' } })
    await user.click(screen.getByRole('button', { name: 'Actualizar' }))

    expect(await screen.findByText('La fecha inicial no puede ser posterior a la final.')).toBeInTheDocument()
    expect(urls('/account/statement')).toHaveLength(before)
  })

  it('shows opening and closing balance and a running balance per movement', async () => {
    ledger.register({ kind: 'Invoice', amount: 100000, concept: 'Compra de carne', occurredOn: daysAgo(10) })
    ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    renderScreen()

    const table = await screen.findByRole('table')
    const rows = within(table).getAllByRole('row')
    expect(within(rows[1]).getByText('Compra de carne')).toBeInTheDocument()
    expect(within(rows[1]).getAllByText(/100\.000,00/).length).toBeGreaterThanOrEqual(1)
    expect(within(rows[2]).getByText('Pago parcial')).toBeInTheDocument()
    expect(within(rows[2]).getByText(/60\.000,00/)).toBeInTheDocument()
    expect(screen.getByTestId('statement-opening')).toHaveTextContent(/0,00/)
    expect(screen.getByTestId('statement-closing')).toHaveTextContent(/60\.000,00/)
  })

  it('marks a reversed movement and links it to its reversal, which cannot be reversed again', async () => {
    const payment = ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    ledger.reverse(payment.id, {})
    renderScreen()

    const table = await screen.findByRole('table')
    const reversedRow = within(table).getAllByRole('row')[1]
    expect(within(reversedRow).getByText('Anulado')).toBeInTheDocument()
    expect(within(reversedRow).getByText('Pago parcial')).toHaveClass('line-through')
    expect(within(reversedRow).getByRole('link', { name: 'Ver anulación' })).toHaveAttribute('href', '#movement-m2')
    expect(within(reversedRow).queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()

    const reversalRow = within(table).getAllByRole('row')[2]
    expect(reversalRow).toHaveAttribute('id', 'movement-m2')
    expect(within(reversalRow).queryByRole('button', { name: 'Anular' })).not.toBeInTheDocument()
    expect(within(reversalRow).queryByText('Anulado')).not.toBeInTheDocument()
  })

  it('registers an invoice and a payment, then reverses the payment: 100.000, 60.000, back to 100.000', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('No hay movimientos en este período.')

    await register(user, { kind: 'Invoice', amount: '100000', concept: 'Compra de carne' })
    await waitFor(() => expect(screen.getByTestId('summary-balance')).toHaveTextContent(/100\.000,00/))
    expect(screen.getByRole('heading', { name: /Cuenta corriente/ })).toBeInTheDocument()

    await register(user, { kind: 'Payment', amount: '40000', concept: 'Pago parcial' })
    await waitFor(() => expect(screen.getByTestId('summary-balance')).toHaveTextContent(/60\.000,00/))
    expect(screen.getByTestId('summary-balance')).toHaveTextContent('Le debemos')

    const paymentRow = within(await screen.findByRole('table')).getByText('Pago parcial').closest('tr')!
    await user.click(within(paymentRow).getByRole('button', { name: 'Anular' }))
    await user.click(await screen.findByRole('button', { name: 'Confirmar anulación' }))

    await waitFor(() => expect(screen.getByTestId('summary-balance')).toHaveTextContent(/100\.000,00/))
    const reversedRow = within(await screen.findByRole('table')).getByText('Pago parcial').closest('tr')!
    expect(within(reversedRow).getByText('Anulado')).toBeInTheDocument()
    expect(screen.getByTestId('statement-closing')).toHaveTextContent(/100\.000,00/)
  })

  it('sends the typed movement and omits the optional fields left empty', async () => {
    const user = userEvent.setup()
    renderScreen()
    await register(user, { kind: 'Invoice', amount: '100000.5', concept: 'Compra de carne' })

    await waitFor(() => expect(postBodies()).toHaveLength(1))
    expect(postBodies()[0]).toEqual({ kind: 'Invoice', amount: 100000.5, concept: 'Compra de carne', occurredOn: today() })
    expect(urls('/account/movements')[0]).toBe(`/suppliers/${SUPPLIER.id}/account/movements`)
  })

  it('sends a due date and a document reference when given', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: '500' } })
    fireEvent.change(screen.getByLabelText('Concepto'), { target: { value: 'Factura A' } })
    fireEvent.change(screen.getByLabelText('Comprobante'), { target: { value: 'A-0001-123' } })
    fireEvent.change(screen.getByLabelText('Vencimiento'), { target: { value: '2099-01-31' } })
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(postBodies()).toHaveLength(1))
    expect(postBodies()[0]).toMatchObject({ documentReference: 'A-0001-123', dueOn: '2099-01-31' })
  })

  it('asks for the effect only on an adjustment, in plain words, and sends it', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))

    expect(screen.queryByLabelText('Efecto del ajuste')).not.toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Adjustment')
    const options = Array.from((screen.getByLabelText('Efecto del ajuste') as HTMLSelectElement).options).map((o) => o.text)
    expect(options).toEqual(['Elegí el efecto', 'Aumenta deuda', 'Disminuye deuda'])

    // No effect is preselected: a wrong default would silently book the adjustment the other way.
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: '10' } })
    fireEvent.change(screen.getByLabelText('Concepto'), { target: { value: 'Corrección' } })
    await user.click(screen.getByRole('button', { name: 'Registrar' }))
    expect(await screen.findByText('Elegí si el ajuste aumenta o disminuye la deuda.')).toBeInTheDocument()
    expect(postBodies()).toHaveLength(0)

    await user.selectOptions(screen.getByLabelText('Efecto del ajuste'), 'Debit')
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(postBodies()).toHaveLength(1))
    expect(postBodies()[0]).toMatchObject({ kind: 'Adjustment', direction: 'Debit' })
  })

  it('offers the movement kinds with their Spanish names and explains the effect of the chosen one', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))

    const options = Array.from((screen.getByLabelText('Tipo de movimiento') as HTMLSelectElement).options).map((o) => o.text)
    expect(options).toEqual(['Saldo inicial', 'Factura', 'Nota de débito', 'Nota de crédito', 'Pago', 'Ajuste'])
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Payment')
    expect(screen.getByText(/disminuye lo que le debemos/i)).toBeInTheDocument()
  })

  it('offers a due date only on movements that increase the debt', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))

    expect(screen.getByLabelText('Vencimiento')).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Payment')
    expect(screen.queryByLabelText('Vencimiento')).not.toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Adjustment')
    await user.selectOptions(screen.getByLabelText('Efecto del ajuste'), 'Debit')
    expect(screen.queryByLabelText('Vencimiento')).not.toBeInTheDocument()
  })

  it.each([
    ['an amount of zero', { amount: '0', concept: 'x' }, 'El importe debe ser mayor que cero y tener hasta 2 decimales.'],
    ['more than two decimals', { amount: '10.123', concept: 'x' }, 'El importe debe ser mayor que cero y tener hasta 2 decimales.'],
    ['no concept', { amount: '10', concept: '' }, 'El concepto es obligatorio.'],
  ])('blocks %s before calling the API', async (_name, values, message) => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: values.amount } })
    fireEvent.change(screen.getByLabelText('Concepto'), { target: { value: values.concept } })
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(await screen.findByText(message)).toBeInTheDocument()
    expect(postBodies()).toHaveLength(0)
  })

  it('blocks a due date before the movement date', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: '10' } })
    fireEvent.change(screen.getByLabelText('Concepto'), { target: { value: 'x' } })
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2026-05-10' } })
    fireEvent.change(screen.getByLabelText('Vencimiento'), { target: { value: '2026-05-09' } })
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    expect(await screen.findByText('El vencimiento no puede ser anterior a la fecha.')).toBeInTheDocument()
    expect(postBodies()).toHaveLength(0)
  })

  it('shows the server message when registering fails and keeps the form', async () => {
    const user = userEvent.setup()
    renderScreen()
    await screen.findByText('No hay movimientos en este período.')
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) =>
      init?.method === 'POST' ? json({ title: 'concept is required' }, 400) : original(url, init),
    )

    await register(user, { kind: 'Invoice', amount: '10', concept: 'x' })

    expect(await screen.findByRole('alert')).toHaveTextContent('concept is required')
    expect(screen.getByLabelText('Importe')).toBeInTheDocument()
  })

  it('lets the operator cancel a reversal without calling the API', async () => {
    ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Anular' }))
    expect(screen.getByRole('alertdialog')).toHaveTextContent(/Pago parcial/)
    await user.click(within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(postBodies()).toHaveLength(0)
  })

  it('sends the optional reversal concept and date', async () => {
    ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Anular' }))
    fireEvent.change(screen.getByLabelText('Concepto de la anulación (opcional)'), { target: { value: 'Pago duplicado' } })
    fireEvent.change(screen.getByLabelText('Fecha de la anulación (opcional)'), { target: { value: today() } })
    await user.click(screen.getByRole('button', { name: 'Confirmar anulación' }))

    await waitFor(() => expect(postBodies()).toHaveLength(1))
    expect(postBodies()[0]).toEqual({ concept: 'Pago duplicado', occurredOn: today() })
    expect(urls('/reverse')[0]).toBe(`/suppliers/${SUPPLIER.id}/account/movements/m1/reverse`)
  })

  it('explains an already reversed movement', async () => {
    ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Anular' }))
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) =>
      init?.method === 'POST' ? json({ error: 'movement-already-reversed' }, 409) : original(url, init),
    )
    await user.click(screen.getByRole('button', { name: 'Confirmar anulación' }))

    expect(await screen.findByText('Este movimiento ya fue anulado.')).toBeInTheDocument()
  })

  it('does not blame an empty account when the statement cannot be loaded', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url === `/suppliers/${SUPPLIER.id}`) return json(SUPPLIER)
      throw new TypeError('down')
    })
    renderScreen()

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.queryByText('No hay movimientos en este período.')).not.toBeInTheDocument()
  })

  it('goes back to the supplier list', async () => {
    const user = userEvent.setup()
    renderScreen()
    await user.click(await screen.findByRole('button', { name: 'Volver a proveedores' }))
    expect(screen.getByText('LISTA DE PROVEEDORES')).toBeInTheDocument()
  })

  it('shows the statement as cards by default on a phone-sized viewport', async () => {
    ledger.register({ kind: 'Payment', amount: 40000, concept: 'Pago parcial', occurredOn: daysAgo(5) })
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: true, media: query, addEventListener() {}, removeEventListener() {} }))
    renderScreen()

    await screen.findByText('Pago parcial')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)
  })
})
