import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TreasuryScreen } from './TreasuryScreen'
import type { TreasuryAccount, TreasuryMovement } from '@/api/treasury'
import type { MasterDataEntry } from '@/api/types'
import { baseConcept, isoDay, parseAmount, subtotals } from '@/treasury/treasuryInput'

describe('TreasuryScreen', () => {
  const fetchMock = vi.fn()

  const type = (id: string, name: string, sortOrder: number, isActive = true): MasterDataEntry => ({
    id,
    organizationId: 'org-1',
    name,
    key: id,
    sortOrder,
    isActive,
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
  })
  const types = [type('t-cash', 'Efectivo', 10), type('t-bank', 'Bancos', 40), type('t-old', 'Viejo', 90, false)]

  const cash: TreasuryAccount = {
    accountId: 'acc-cash',
    branchId: 'branch-1',
    branchName: 'Arrecifes',
    kind: 'Cash',
    name: 'Caja efectivo · Arrecifes',
    balance: 12_000,
    todayIn: 15_000,
    todayOut: 3_000,
    accountTypeId: 't-cash',
    accountTypeName: 'Efectivo',
    isActive: true,
    isAutomatic: true,
  }
  const bank: TreasuryAccount = {
    accountId: 'acc-bank',
    branchId: null,
    branchName: null,
    kind: 'Bank',
    name: 'Banco Nación',
    balance: 100_000,
    todayIn: 0,
    todayOut: 0,
    accountTypeId: 't-bank',
    accountTypeName: 'Bancos',
    isActive: true,
    isAutomatic: false,
  }
  const closed: TreasuryAccount = { ...bank, accountId: 'acc-closed', name: 'Banco cerrado', balance: 0, isActive: false }
  const manual: TreasuryMovement = {
    movementId: 'mov-1',
    accountId: 'acc-cash',
    kind: 'ManualIn',
    direction: 'In',
    amount: 2_000,
    occurredAtUtc: '2026-10-06T12:00:00Z',
    businessDate: '2026-10-06',
    concept: 'Fondo de cambio',
    documentReference: null,
    customerId: null,
    customerName: null,
    sourceType: null,
    reversesMovementId: null,
    reversed: false,
    canVoid: true,
    canEdit: true,
  }
  const sale: TreasuryMovement = {
    ...manual,
    movementId: 'mov-2',
    kind: 'Sale',
    concept: 'Venta 0001-00000012',
    canVoid: false,
    canEdit: false,
    canReclassify: true,
  }
  const voided: TreasuryMovement = {
    ...manual,
    movementId: 'mov-3',
    kind: 'CashWithdrawal',
    direction: 'Out',
    amount: 500,
    concept: 'Retiro para pago de gasto (POS): luz',
    voided: true,
    voidReason: 'No se retiró',
    canVoid: false,
    canEdit: false,
  }

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`
      switch (key) {
        case 'GET /treasury/accounts':
          return json([cash, bank, closed])
        case 'GET /treasury/account-types':
          return json(types)
        case 'GET /account/branch-profiles':
          return json([{ branchId: 'branch-1', name: 'Arrecifes', code: 1 }])
        case 'GET /treasury/accounts/acc-bank/movements':
          return json([])
        case 'GET /treasury/accounts/acc-cash/movements':
          return json([manual, sale, voided])
        case 'POST /treasury/accounts':
          return json({ ...bank, accountId: 'acc-new', name: 'Visa empresa' }, 201)
        case 'POST /treasury/transfers':
          return json({ transferId: 't-1' })
        case 'POST /treasury/movements/mov-1/void':
          return new Response(null, { status: 204 })
        case 'PUT /treasury/movements/mov-1':
        case 'PUT /treasury/movements/mov-2':
          return json({ movementId: 'mov-9' })
        case 'GET /treasury/recurrences':
          return json([
            {
              recurrenceId: 'rec-1', accountId: 'acc-bank', accountName: 'Banco Nación', direction: 'Out', amount: 45000,
              concept: 'Luz (Edenor)', documentReference: null, frequency: 'Monthly', interval: 1, startDate: '2026-07-10',
              endMode: 'Never', endDate: null, maxOccurrences: null, isActive: true, occurrencesRecorded: 4,
              lastRecordedOn: '2026-10-10', nextOn: '2026-11-10', updatedAtUtc: '2026-10-10T00:00:00Z',
            },
          ])
        case 'POST /treasury/recurrences':
          return json({ recurrenceId: 'rec-2' }, 201)
        case 'POST /treasury/reprocess':
          return json({ movementsAdded: 7 })
        case 'POST /treasury/accounts/acc-cash/active':
          return json({ error: 'automatic-account' }, 409)
        default:
          throw new TypeError(`unrouted ${key}`)
      }
    })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const callsTo = (key: string) =>
    fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)
  const bodyOf = (key: string) => JSON.parse(callsTo(key)[0][1].body as string)

  it('shows where the money is: the total, by type, by branch, and the active accounts grouped by type', async () => {
    render(<TreasuryScreen />)

    const totalCard = (await screen.findByText('Total del negocio')).parentElement!
    expect(within(totalCard).getByText(/112\.000,00/)).toBeInTheDocument()
    const byType = screen.getByText('Por tipo de cuenta').parentElement!
    expect(within(byType).getByText('Efectivo')).toBeInTheDocument()
    expect(within(byType).getByText('Bancos')).toBeInTheDocument()
    const byBranch = screen.getByText('Por sucursal').parentElement!
    expect(within(byBranch).getByText('Toda la empresa')).toBeInTheDocument()
    expect(within(byBranch).getByText('Arrecifes')).toBeInTheDocument()

    expect(screen.queryByText('Banco cerrado')).not.toBeInTheDocument()
    await userEvent.setup().click(screen.getByLabelText('Mostrar cuentas inactivas'))
    expect(screen.getByText('Banco cerrado')).toBeInTheDocument()
  })

  it('creates an account of a type of the catalog, offering only active types', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    await user.click(await screen.findByRole('button', { name: 'Nueva cuenta' }))
    const typeSelect = screen.getByLabelText('Tipo de cuenta')
    expect(within(typeSelect).queryByText('Viejo')).not.toBeInTheDocument()
    await user.type(screen.getByLabelText('Nombre'), 'Visa empresa')
    await user.selectOptions(typeSelect, 't-bank')
    await user.click(screen.getByRole('button', { name: 'Crear cuenta' }))

    await waitFor(() => expect(callsTo('POST /treasury/accounts')).toHaveLength(1))
    expect(bodyOf('POST /treasury/accounts')).toEqual({ name: 'Visa empresa', accountTypeId: 't-bank' })
  })

  it('records a transfer between two accounts', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    await user.click(await screen.findByRole('button', { name: 'Registrar movimiento' }))
    await user.selectOptions(screen.getByLabelText('Tipo'), 'Transfer')
    await user.selectOptions(screen.getByLabelText('Desde la cuenta'), 'acc-cash')
    await user.selectOptions(screen.getByLabelText('Hacia la cuenta'), 'acc-bank')
    await user.type(screen.getByLabelText('Importe'), '5000,50')
    await user.type(screen.getByLabelText('Concepto'), 'Depósito de la recaudación')
    await user.click(screen.getByRole('button', { name: 'Registrar' }))

    await waitFor(() => expect(callsTo('POST /treasury/transfers')).toHaveLength(1))
    expect(bodyOf('POST /treasury/transfers')).toEqual({
      fromAccountId: 'acc-cash',
      toAccountId: 'acc-bank',
      amount: 5000.5,
      date: isoDay(new Date()),
      concept: 'Depósito de la recaudación',
    })
  })

  it('voids a movement with a reason, and shows a voided one crossed out with its reason', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    expect(await screen.findByText('Fondo de cambio')).toBeInTheDocument()
    expect(screen.getByText('Anulado: No se retiró')).toBeInTheDocument()
    expect(screen.getByText('Retiro para pago de gasto (POS): luz')).toHaveClass('line-through')
    expect(screen.getAllByRole('button', { name: 'Anular' })).toHaveLength(1) // only the manual one

    await user.click(screen.getByRole('button', { name: 'Anular' }))
    await user.click(screen.getByRole('button', { name: 'Anular movimiento' }))
    expect(screen.getByText('Escribí el motivo de la anulación.')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Motivo de la anulación'), 'Cargado dos veces')
    await user.click(screen.getByRole('button', { name: 'Anular movimiento' }))

    await waitFor(() => expect(callsTo('POST /treasury/movements/mov-1/void')).toHaveLength(1))
    expect(bodyOf('POST /treasury/movements/mov-1/void')).toEqual({ reason: 'Cargado dos veces' })
  })

  it('edits a movement sending only what changed, and a sale only changes account', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    const amount = screen.getByLabelText('Importe')
    await user.clear(amount)
    await user.type(amount, '2500')
    await user.type(screen.getByLabelText('Motivo del cambio'), 'Era 2.500')
    await user.click(screen.getByRole('button', { name: 'Guardar corrección' }))
    await waitFor(() => expect(callsTo('PUT /treasury/movements/mov-1')).toHaveLength(1))
    expect(bodyOf('PUT /treasury/movements/mov-1')).toEqual({ amount: 2500, reason: 'Era 2.500' })

    await user.click(await screen.findByRole('button', { name: 'Cambiar cuenta' }))
    expect(screen.queryByLabelText('Importe')).not.toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Cuenta'), 'acc-bank')
    await user.type(screen.getByLabelText('Motivo del cambio'), 'Se cobró por transferencia')
    await user.click(screen.getByRole('button', { name: 'Guardar corrección' }))
    await waitFor(() => expect(callsTo('PUT /treasury/movements/mov-2')).toHaveLength(1))
    expect(bodyOf('PUT /treasury/movements/mov-2')).toEqual({ accountId: 'acc-bank', reason: 'Se cobró por transferencia' })
  })

  it('never offers to deactivate the drawer of a branch', async () => {
    render(<TreasuryScreen />)

    expect(await screen.findByRole('button', { name: 'Editar cuenta' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Desactivar' })).not.toBeInTheDocument()
  })

  it('adds up subtotals, reads amounts and strips the transfer prefix', () => {
    expect(subtotals([cash, bank, { ...cash, accountId: 'x', balance: 0.1 }], (a) => ({ key: a.accountTypeId!, label: a.accountTypeName! }))).toEqual([
      { key: 't-cash', label: 'Efectivo', total: 12_000.1 },
      { key: 't-bank', label: 'Bancos', total: 100_000 },
    ])
    expect(parseAmount('$ 15000,5')).toBe(15000.5)
    expect(parseAmount('1,555')).toBeNull()
    expect(baseConcept({ ...manual, kind: 'Transfer', concept: 'Transferencia a Banco: Depósito: lunes' })).toBe('Depósito: lunes')
  })

  it('takes the operator to the form it opens, with the cursor in its first field', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    await user.click(await screen.findByRole('button', { name: 'Editar cuenta' }))

    expect(screen.getByRole('heading', { name: 'Editar cuenta' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveFocus()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Caja efectivo · Arrecifes')
  })

  it('brings in the operations received before the treasury existed', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    await user.click(await screen.findByRole('button', { name: 'Traer operaciones anteriores' }))

    expect(await screen.findByText('Se incorporaron 7 movimientos de operaciones anteriores.')).toBeInTheDocument()
    expect(callsTo('POST /treasury/reprocess')).toHaveLength(1)
  })

  it('lists the recurring expenses with when they repeat, and creates one that never ends', async () => {
    const user = userEvent.setup()
    render(<TreasuryScreen />)

    expect(await screen.findByText('Luz (Edenor)')).toBeInTheDocument()
    expect(screen.getByText('Todos los meses, el día 10')).toBeInTheDocument()
    expect(screen.getByText('10/11/2026')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Nuevo recurrente' }))
    await user.type(screen.getByLabelText('Concepto'), 'Internet')
    await user.type(screen.getByLabelText('Importe'), '25000')
    await user.selectOptions(screen.getByLabelText('Sale de la cuenta'), 'acc-bank')
    expect(screen.getByText(/Próximas fechas:/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Crear recurrente' }))

    await waitFor(() => expect(callsTo('POST /treasury/recurrences')).toHaveLength(1))
    expect(bodyOf('POST /treasury/recurrences')).toMatchObject({
      accountId: 'acc-bank', direction: 'Out', amount: 25000, concept: 'Internet', frequency: 'Monthly', interval: 1, endMode: 'Never',
      startDate: isoDay(new Date()), includePastDates: false,
    })
    expect(await screen.findByText('"Internet" creado: se registra solo en cada fecha.')).toBeInTheDocument()
  })
})
