import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CustomerAccountScreen } from './SupplierAccountScreen'
import type { AccountStatement, AccountSummary } from '@/api/types'

/**
 * The customer's current account reuses the supplier ledger screen with the customer's sign convention: a positive
 * balance is what the customer owes ("Nos debe"), a sale or delivery on account increases it (Debit) and a payment
 * received ("Cobro") lowers it.
 */
describe('CustomerAccountScreen', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
  const customerId = 'c-1'

  const summary: AccountSummary = {
    asOf: '2026-10-05',
    balance: 26500,
    overdue: 0,
    current: 26500,
    aging: { d0_30: 0, d31_60: 0, d61_90: 0, d90plus: 0 },
  }
  const statement: AccountStatement = {
    openingBalance: 0,
    closingBalance: 26500,
    movements: [
      {
        id: 'm-1',
        supplierId: null,
        customerId,
        kind: 'Invoice',
        direction: 'Debit',
        amount: 26500,
        occurredOn: '2026-10-05',
        dueOn: null,
        documentReference: 'R01-00000001',
        concept: 'Remito R01-00000001 · Pedido P01-W-12',
        reversesMovementId: null,
        createdAtUtc: '2026-10-05T15:00:00Z',
        createdByUserId: 'u-1',
        runningBalance: 26500,
        reversed: false,
        reversedByMovementId: null,
      },
    ],
  }

  const callsTo = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)

  beforeEach(() => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const path = url.split('?')[0]
      if (path === `/customers/${customerId}`) return json({ id: customerId, displayName: 'Parrilla Don Julio' })
      if (path === `/customers/${customerId}/account/summary`) return json(summary)
      if (path === `/customers/${customerId}/account/statement`) return json(statement)
      if (path === `/customers/${customerId}/account/movements` && init?.method === 'POST') return json({ id: 'm-2' }, 201)
      throw new TypeError(`unrouted ${url}`)
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
      <MemoryRouter initialEntries={[`/app/customers/${customerId}/account`]}>
        <Routes>
          <Route path="/app/customers/:id/account" element={<CustomerAccountScreen />} />
        </Routes>
      </MemoryRouter>,
    )

  it("shows what the customer owes and its deliveries on account as increases of the debt", async () => {
    renderScreen()

    expect(await screen.findByRole('heading', { name: 'Cuenta corriente: Parrilla Don Julio' })).toBeInTheDocument()
    expect(screen.getByTestId('summary-balance')).toHaveTextContent('Nos debe')
    expect(screen.getByText('Remito R01-00000001 · Pedido P01-W-12')).toBeInTheDocument()
    expect(screen.getAllByText('Aumenta deuda').length).toBeGreaterThan(0)
    expect(screen.getByRole('button', { name: 'Volver a clientes' })).toBeInTheDocument()
  })

  it('records a payment received, and offers an adjustment that increases the debt as a Debit', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Adjustment')
    const effect = screen.getByLabelText('Efecto del ajuste')
    expect(Array.from((effect as HTMLSelectElement).options).map((option) => [option.value, option.text])).toEqual([
      ['', 'Elegí el efecto'],
      ['Debit', 'Aumenta deuda'],
      ['Credit', 'Disminuye deuda'],
    ])

    await user.selectOptions(screen.getByLabelText('Tipo de movimiento'), 'Payment')
    expect(screen.getByRole('option', { name: 'Cobro' })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Importe'), '10000')
    await user.type(screen.getByLabelText('Concepto'), 'Cobro en efectivo')
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(callsTo(`POST /customers/${customerId}/account/movements`)).toHaveLength(1))
    expect(JSON.parse(callsTo(`POST /customers/${customerId}/account/movements`)[0][1].body as string)).toMatchObject({
      kind: 'Payment',
      amount: 10000,
      concept: 'Cobro en efectivo',
    })
  })
})
