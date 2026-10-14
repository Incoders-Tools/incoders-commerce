import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router'
import type { EmployeeRecord, PayrollRunDetail } from '@/api/employees'
import type { TreasuryAccount } from '@/api/treasury'
import { BranchContext } from '@/branch/BranchContext'
import { EmployeesScreen } from './EmployeesScreen'
import { PayrollRunScreen } from './PayrollRunScreen'

/**
 * The staff and the payroll, web: the staff list with what each one is owed or owes, creating an employee who takes
 * goods, an advance from a treasury account, and a draft payroll edited (overtime, the goods discount) and paid.
 */
describe('staff and payroll screens', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const juan: EmployeeRecord = {
    id: 'e-1',
    branchId: 'b-1',
    branchName: 'Arrecifes',
    fileNumber: 1,
    firstName: 'Juan',
    lastName: 'Pérez',
    fullName: 'Pérez, Juan',
    documentNumber: '30111222',
    cuil: null,
    roleId: null,
    roleName: 'Carnicero',
    phone: null,
    email: null,
    address: null,
    hireDate: '2025-01-01',
    terminationDate: null,
    payFrequency: 'Monthly',
    baseSalary: 500000,
    customerId: 'c-1',
    notes: null,
    isActive: true,
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    balance: -100000,
    purchasesOwed: 50000,
    overtimeHourlyRate: null,
    terminationReason: null,
    overtimeToPay: 0,
    overtimeHoursToPay: 0,
    payToDate: 0,
  }
  const cash: TreasuryAccount = {
    accountId: 'acc-cash', branchId: 'b-1', branchName: 'Arrecifes', kind: 'Cash', name: 'Caja efectivo · Arrecifes',
    balance: 0, todayIn: 0, todayOut: 0, isActive: true,
  }
  const bank: TreasuryAccount = { ...cash, accountId: 'acc-bank', kind: 'Bank', name: 'Banco Nación' }

  function routeFetch(routes: Record<string, (init?: RequestInit) => Response>) {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`
      const handler = routes[key]
      if (!handler) throw new TypeError(`unrouted ${key}`)
      return handler(init)
    })
  }

  const callsTo = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)
  const bodyOf = (key: string) => JSON.parse(callsTo(key)[0][1].body as string)

  const branches = { selectedBranch: { id: 'b-1', name: 'Arrecifes', code: 1 }, selectableBranches: [{ id: 'b-1', name: 'Arrecifes', code: 1 }], selectBranch: () => {} }

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const renderEmployees = () =>
    render(
      <MemoryRouter>
        <BranchContext.Provider value={branches as never}>
          <EmployeesScreen />
        </BranchContext.Provider>
      </MemoryRouter>,
    )

  it('lists the staff with what each one owes, and creates an employee who takes goods', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'GET /employees/next-file-number': () => json({ fileNumber: 2 }),
      'POST /employees': () => json({ ...juan, id: 'e-2', firstName: 'Ana', fullName: 'Gómez, Ana', fileNumber: 2 }, 201),
    })
    const user = userEvent.setup()
    renderEmployees()

    expect(await screen.findByText('Pérez, Juan')).toBeInTheDocument()
    expect(screen.getByText('0001')).toBeInTheDocument() // employee-lifecycle E1: the legajo zero-padded
    expect(screen.getByText('Nos debe $ 100.000,00'.replace(/ /g, ' '))).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Nuevo empleado' }))
    // The form tells which file number the new employee will get while it is left empty.
    await waitFor(() => expect(screen.getByLabelText('Legajo')).toHaveAttribute('placeholder', 'Automático (el siguiente: 0002)'))
    expect(screen.getByLabelText('Legajo')).toHaveValue('')
    await user.type(screen.getByLabelText('Apellido'), 'Gómez')
    await user.type(screen.getByLabelText('Nombre'), 'Ana')
    await user.type(screen.getByLabelText('Sueldo pactado por período'), '650000')
    await user.type(screen.getByLabelText('Valor de la hora extra'), '2500,5') // employee-lifecycle E2
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(callsTo('POST /employees')).toHaveLength(1))
    expect(bodyOf('POST /employees')).toMatchObject({
      branchId: 'b-1', firstName: 'Ana', lastName: 'Gómez', payFrequency: 'Monthly', baseSalary: 650000, takesGoods: true,
      overtimeHourlyRate: 2500.5,
    })
    expect(await screen.findByText('Gómez, Ana guardado (legajo 0002).')).toBeInTheDocument()
  })

  it('edits an employee whose legajo shows padded, and sends it back as the same number', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'PUT /employees/e-1': () => json(juan),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    expect(screen.getByLabelText('Legajo')).toHaveValue('0001')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(callsTo('PUT /employees/e-1')).toHaveLength(1))
    expect(bodyOf('PUT /employees/e-1').fileNumber).toBe(1)
    expect(bodyOf('PUT /employees/e-1').overtimeHourlyRate).toBe(0) // an empty rate clears it
    expect(callsTo('GET /employees/next-file-number')).toHaveLength(0) // only a new employee asks
  })

  it('keeps the plain placeholder when the next legajo cannot be read', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'GET /employees/next-file-number': () => json({ error: 'boom' }, 500),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Nuevo empleado' }))
    await waitFor(() => expect(callsTo('GET /employees/next-file-number')).toHaveLength(1))
    expect(screen.getByLabelText('Legajo')).toHaveAttribute('placeholder', 'Automático (el siguiente)')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('refuses an overtime rate that is not a positive amount, without saving', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'GET /employees/next-file-number': () => json({ fileNumber: 2 }),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Nuevo empleado' }))
    await user.type(screen.getByLabelText('Apellido'), 'Gómez')
    await user.type(screen.getByLabelText('Nombre'), 'Ana')
    await user.type(screen.getByLabelText('Valor de la hora extra'), 'dos mil')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(screen.getByRole('alert')).toHaveTextContent('Escribí un valor mayor que cero, con hasta dos decimales, o dejalo vacío.')
    expect(callsTo('POST /employees')).toHaveLength(0)
  })

  it('opens "Gestionar baja" of an employee from its row, instead of a bare Dar de baja', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Acciones de Pérez, Juan' }))
    expect(screen.queryByRole('menuitem', { name: 'Dar de baja' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('menuitem', { name: 'Gestionar baja' }))
    expect(await screen.findByRole('heading', { name: 'Baja de Pérez, Juan' })).toBeInTheDocument()
  })

  it('opens the overtime of an employee from its row', async () => {
    routeFetch({
      'GET /employees': () => json([{ ...juan, overtimeHourlyRate: 2000 }]),
      'GET /employees/roles': () => json([]),
      'GET /employees/e-1/overtime': () => json([]),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Acciones de Pérez, Juan' }))
    await user.click(screen.getByRole('menuitem', { name: 'Horas extra' }))
    expect(await screen.findByRole('heading', { name: 'Horas extra de Pérez, Juan' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Volver al personal' }))
    expect(await screen.findByText('Pérez, Juan')).toBeInTheDocument()
  })

  it('lists the overtime each employee has still to be paid', async () => {
    routeFetch({
      'GET /employees': () => json([{ ...juan, overtimeToPay: 40500, overtimeHoursToPay: 4.5 }, { ...juan, id: 'e-2', fullName: 'Gómez, Ana' }]),
      'GET /employees/roles': () => json([]),
    })
    renderEmployees()

    const juanRow = (await screen.findByText('Pérez, Juan')).closest('tr')!
    expect(within(juanRow).getByText(/40\.500,00 \(4,5 h\)/)).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Horas extra a pagar/ })).toBeInTheDocument()
  })

  // owner feedback round 2, R3: the advances apart from what each employee has to collect to date.
  it('lists the advances and what each employee has to collect to date', async () => {
    routeFetch({
      'GET /employees': () =>
        json([{ ...juan, balance: -15000, payToDate: 485000 }, { ...juan, id: 'e-2', fullName: 'Gómez, Ana', balance: -60000, payToDate: -10000 }]),
      'GET /employees/roles': () => json([]),
    })
    renderEmployees()

    const juanRow = (await screen.findByText('Pérez, Juan')).closest('tr')!
    expect(within(juanRow).getByText(/Nos debe \$\s15\.000,00/)).toBeInTheDocument()
    expect(within(juanRow).getByText(/^\$\s485\.000,00$/)).toBeInTheDocument()
    const anaRow = screen.getByText('Gómez, Ana').closest('tr')!
    expect(within(anaRow).getByText(/Nos debe \$\s10\.000,00/)).toHaveClass('text-destructive')
    expect(screen.getByRole('columnheader', { name: /Adelantos y saldo/ })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /A cobrar a hoy/ })).toBeInTheDocument()
  })

  it('opens the goods of an employee from its row', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'GET /employees/e-1/goods': () => json([]),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Acciones de Pérez, Juan' }))
    await user.click(screen.getByRole('menuitem', { name: 'Mercadería' }))
    expect(await screen.findByRole('heading', { name: 'Mercadería de Pérez, Juan' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Volver al personal' }))
    expect(await screen.findByText('Pérez, Juan')).toBeInTheDocument()
  })

  it('hands an advance from the branch drawer', async () => {
    routeFetch({
      'GET /employees': () => json([juan]),
      'GET /employees/roles': () => json([]),
      'GET /treasury/accounts': () => json([cash]),
      'POST /employees/e-1/advances': () => json({ advanceId: 'a-1' }),
    })
    const user = userEvent.setup()
    renderEmployees()

    await user.click(await screen.findByRole('button', { name: 'Acciones de Pérez, Juan' }))
    await user.click(screen.getByRole('menuitem', { name: 'Adelanto' }))
    await user.type(screen.getByLabelText('Importe'), '50000')
    await waitFor(() => expect(screen.getByLabelText('Sale de')).toHaveValue('acc-cash'))
    await user.click(screen.getByRole('button', { name: 'Registrar adelanto' }))

    await waitFor(() => expect(callsTo('POST /employees/e-1/advances')).toHaveLength(1))
    expect(bodyOf('POST /employees/e-1/advances')).toMatchObject({ amount: 50000, accountId: 'acc-cash' })
  })

  const draft: PayrollRunDetail = {
    run: {
      runId: 'r-1', runNumber: 1, branchId: 'b-1', periodFrom: '2026-10-01', periodTo: '2026-10-31', payFrequency: 'Monthly',
      status: 'Draft', notes: null, employeeCount: 1, totalNet: 355000, paidOn: null, paymentAccountId: null, createdAtUtc: '2026-10-31T00:00:00Z',
    },
    branchName: 'Arrecifes',
    paymentAccountName: null,
    payslips: [
      {
        payslipId: 'p-1', employeeId: 'e-1', fileNumber: 1, employeeName: 'Pérez, Juan', roleName: 'Carnicero', cuil: null, takesGoods: true,
        lines: [
          { lineNo: 1, kind: 'Earning', source: 'BaseSalary', concept: 'Sueldo básico', amount: 500000 },
          { lineNo: 2, kind: 'Deduction', source: 'Advances', concept: 'Adelantos y saldo pendiente', amount: 100000 },
        ],
        goodsItems: [],
        purchasesAmount: 50000, purchasesDiscountPercent: 0, purchasesItemsDiscount: 0, purchasesDiscount: 0, purchasesDeducted: 50000,
        earnings: 500000, deductions: 100000, net: 350000, netPaid: 350000, payments: [], paid: 0,
      },
    ],
  }

  const goodsItem = (id: string, productName: string, lineTotal: number, sourceType: 'Web' | 'PosSale' = 'Web') => ({
    id, sourceType, sourceId: `s-${id}`, lineNo: 1, occurredOn: '2026-10-12', presentationId: `p-${id}`, productName,
    presentationName: 'Por kg', quantity: 1, unitPrice: lineTotal, lineTotal, discountPercent: 0, status: 'InPayroll' as const, runNumber: 1,
  })

  const withGoods: PayrollRunDetail = {
    ...draft,
    payslips: [{
      ...draft.payslips[0],
      purchasesAmount: 30000,
      purchasesDeducted: 30000,
      goodsItems: [goodsItem('g-1', 'Asado', 15000), goodsItem('g-2', 'Chorizo', 9000, 'PosSale')],
    }],
  }

  const renderRun = () =>
    render(
      <MemoryRouter initialEntries={['/app/payroll/r-1']}>
        <Routes>
          <Route path="/app/payroll/:id" element={<PayrollRunScreen />} />
        </Routes>
      </MemoryRouter>,
    )

  it('discounts the goods item by item and the rest by a percent, and sends each item its discount', async () => {
    routeFetch({
      'GET /payroll/runs/r-1': () => json(withGoods),
      'PUT /payroll/runs/r-1/payslips/p-1': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderRun()

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    const goods = screen.getByRole('list', { name: 'Mercadería de Pérez, Juan' })
    expect(within(goods).getByText(/12\/10\/2026 · 1 · Venta POS/)).toBeInTheDocument()
    expect(screen.getByText(/Sin detalle de productos: .*6\.000,00/)).toBeInTheDocument()
    await user.type(screen.getByLabelText('Bonificación % de Asado Por kg'), '20')
    await user.type(screen.getByLabelText('Bonificación % sobre el resto'), '10')
    // 20 % of the asado (3.000), then 10 % of the remaining 27.000 (2.700).
    expect(screen.getByText(/Se le bonifica .*5\.700,00 y se le descuenta .*24\.300,00 del sueldo\./)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Guardar recibo' }))

    await waitFor(() => expect(callsTo('PUT /payroll/runs/r-1/payslips/p-1')).toHaveLength(1))
    expect(bodyOf('PUT /payroll/runs/r-1/payslips/p-1')).toMatchObject({
      purchasesDiscountPercent: 10,
      goodsItems: [{ itemId: 'g-1', discountPercent: 20 }, { itemId: 'g-2', discountPercent: 0 }],
    })
  })

  it('lists the goods among the deductions as a fixed line that follows the discounts, and never sends it as a line', async () => {
    routeFetch({
      'GET /payroll/runs/r-1': () => json(withGoods),
      'PUT /payroll/runs/r-1/payslips/p-1': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderRun()

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    const lines = screen.getByRole('list', { name: 'Haberes y descuentos de Pérez, Juan' })
    const goods = within(lines).getByRole('listitem', { name: 'Mercadería (con bonificación)' })
    expect(within(goods).getByText(/30\.000,00/)).toBeInTheDocument()
    expect(within(goods).getByText('Se calcula con la mercadería que llevó y la bonificación de abajo.')).toBeInTheDocument()
    expect(within(goods).queryByRole('button', { name: 'Quitar' })).not.toBeInTheDocument()

    await user.type(screen.getByLabelText('Bonificación % de Asado Por kg'), '20')
    await user.type(screen.getByLabelText('Bonificación % sobre el resto'), '10')
    expect(within(goods).getByText(/24\.300,00/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Guardar recibo' }))
    await waitFor(() => expect(callsTo('PUT /payroll/runs/r-1/payslips/p-1')).toHaveLength(1))
    expect(bodyOf('PUT /payroll/runs/r-1/payslips/p-1').lines.map((line: { source: string }) => line.source)).toEqual(['BaseSalary', 'Advances'])
  })

  async function openPayment() {
    routeFetch({
      'GET /payroll/runs/r-1': () => json(draft),
      'GET /treasury/accounts': () => json([cash, bank]),
      'POST /payroll/runs/r-1/pay': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    renderRun()
    await user.click(await screen.findByRole('button', { name: 'Pagar liquidación' }))
    await waitFor(() => expect(screen.getByLabelText('Se paga desde')).toHaveValue('acc-cash'))
    await user.click(screen.getByRole('button', { name: 'Dividir el pago de Pérez, Juan' }))
    return { user, split: screen.getByRole('listitem', { name: 'Pago de Pérez, Juan' }) }
  }

  it('pays a payslip from two accounts, part by part', async () => {
    const { user, split } = await openPayment()

    // The split starts as the whole net from the run's account.
    expect(within(split).getByLabelText('Cuenta 1 de Pérez, Juan')).toHaveValue('acc-cash')
    expect(within(split).getByLabelText('Importe 1 de Pérez, Juan')).toHaveValue('350000')
    await user.clear(within(split).getByLabelText('Importe 1 de Pérez, Juan'))
    await user.type(within(split).getByLabelText('Importe 1 de Pérez, Juan'), '200000')
    // The next part takes another account and what is left.
    await user.click(within(split).getByRole('button', { name: '+ Agregar cuenta' }))
    expect(within(split).getByLabelText('Cuenta 2 de Pérez, Juan')).toHaveValue('acc-bank')
    expect(within(split).getByLabelText('Importe 2 de Pérez, Juan')).toHaveValue('150000')
    expect(within(split).getByText(/Se paga .*350\.000,00 de .*350\.000,00\./)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Pagar' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Pagar' }))
    await waitFor(() => expect(callsTo('POST /payroll/runs/r-1/pay')).toHaveLength(1))
    expect(bodyOf('POST /payroll/runs/r-1/pay')).toMatchObject({
      accountId: 'acc-cash',
      splits: [{ payslipId: 'p-1', parts: [{ accountId: 'acc-cash', amount: 200000 }, { accountId: 'acc-bank', amount: 150000 }] }],
    })
  })

  it('does not let the parts exceed the net, and says what they leave owed to the employee', async () => {
    const { user, split } = await openPayment()
    const amount = within(split).getByLabelText('Importe 1 de Pérez, Juan')

    await user.clear(amount)
    await user.type(amount, '400000')
    expect(within(split).getByText(/Supera el neto por .*50\.000,00\./)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pagar' })).toBeDisabled()

    await user.clear(amount)
    await user.type(amount, '300000')
    expect(within(split).getByText(/Quedan .*50\.000,00 a favor del empleado \(Le debemos\)\./)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pagar' })).toBeEnabled()

    // Back to the whole net from one account: no split is sent.
    await user.click(within(split).getByRole('button', { name: 'Todo desde una cuenta' }))
    await user.click(screen.getByRole('button', { name: 'Pagar' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Pagar' }))
    await waitFor(() => expect(callsTo('POST /payroll/runs/r-1/pay')).toHaveLength(1))
    expect(bodyOf('POST /payroll/runs/r-1/pay').splits).toBeUndefined()
  })

  it('reads what paid each payslip of a paid run, and what stayed owed', async () => {
    const paid: PayrollRunDetail = {
      ...draft,
      run: { ...draft.run, status: 'Paid', paidOn: '2026-10-31', paymentAccountId: 'acc-cash' },
      paymentAccountName: 'Caja efectivo · Arrecifes',
      payslips: [{
        ...draft.payslips[0],
        payments: [
          { accountId: 'acc-cash', accountName: 'Caja efectivo · Arrecifes', amount: 200000 },
          { accountId: 'acc-bank', accountName: 'Banco Nación', amount: 100000 },
        ],
        paid: 300000,
      }],
    }
    routeFetch({ 'GET /payroll/runs/r-1': () => json(paid) })
    renderRun()

    expect(await screen.findByText('Pagada el 31/10/2026 desde varias cuentas')).toBeInTheDocument()
    expect(screen.getByText('Pagado desde Banco Nación')).toBeInTheDocument()
    expect(screen.getByText(/Quedó a favor del empleado/)).toBeInTheDocument()
  })

  it('refuses an item discount that is not a percentage of 0 to 100, without saving', async () => {
    routeFetch({ 'GET /payroll/runs/r-1': () => json(withGoods) })
    const user = userEvent.setup()
    renderRun()

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    await user.type(screen.getByLabelText('Bonificación % de Chorizo Por kg'), '120')
    await user.click(screen.getByRole('button', { name: 'Guardar recibo' }))
    expect(await screen.findByText('La bonificación es un porcentaje de 0 a 100.')).toBeInTheDocument()
    expect(callsTo('PUT /payroll/runs/r-1/payslips/p-1')).toHaveLength(0)
  })

  it('reads the goods of a paid payslip with the discount it got', async () => {
    const paid: PayrollRunDetail = {
      ...withGoods,
      run: { ...withGoods.run, status: 'Paid', paidOn: '2026-10-31' },
      payslips: [{ ...withGoods.payslips[0], purchasesItemsDiscount: 3000, purchasesDiscount: 5700, purchasesDeducted: 24300 }],
    }
    routeFetch({ 'GET /payroll/runs/r-1': () => json(paid) })
    renderRun()

    expect(await screen.findByText(/de .*30\.000,00 con .*5\.700,00 de bonificación/)).toBeInTheDocument()
  })

  it('shows the overtime the payroll took as a fixed line that can only be taken out', async () => {
    const withOvertime: PayrollRunDetail = {
      ...draft,
      payslips: [{
        ...draft.payslips[0],
        lines: [...draft.payslips[0].lines, { lineNo: 3, kind: 'Earning', source: 'Overtime', concept: 'Horas extra (4 h)', amount: 16000 }],
      }],
    }
    routeFetch({
      'GET /payroll/runs/r-1': () => json(withOvertime),
      'PUT /payroll/runs/r-1/payslips/p-1': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    render(
      <MemoryRouter initialEntries={['/app/payroll/r-1']}>
        <Routes>
          <Route path="/app/payroll/:id" element={<PayrollRunScreen />} />
        </Routes>
      </MemoryRouter>,
    )

    await user.click(await screen.findByRole('button', { name: 'Editar' }))
    const lines = screen.getByRole('list', { name: 'Haberes y descuentos de Pérez, Juan' })
    const overtime = within(lines).getAllByRole('listitem')[2]
    expect(within(overtime).getByLabelText('Concepto')).toHaveAttribute('readonly')
    expect(within(overtime).getByLabelText('Importe')).toHaveAttribute('readonly')
    expect(within(overtime).getByText('Se calcula con las horas extra cargadas.')).toBeInTheDocument()

    await user.click(within(overtime).getByRole('button', { name: 'Quitar' }))
    await user.click(screen.getByRole('button', { name: 'Guardar recibo' }))
    await waitFor(() => expect(callsTo('PUT /payroll/runs/r-1/payslips/p-1')).toHaveLength(1))
    expect(bodyOf('PUT /payroll/runs/r-1/payslips/p-1').lines.map((line: { source: string }) => line.source)).toEqual(['BaseSalary', 'Advances'])
  })

  it('edits a draft payslip with overtime and the goods discount, then pays it from a treasury account', async () => {
    routeFetch({
      'GET /payroll/runs/r-1': () => json(draft),
      'PUT /payroll/runs/r-1/payslips/p-1': () => json(undefined, 204),
      'GET /treasury/accounts': () => json([cash]),
      'POST /payroll/runs/r-1/pay': () => json(undefined, 204),
    })
    const user = userEvent.setup()
    render(
      <MemoryRouter initialEntries={['/app/payroll/r-1']}>
        <Routes>
          <Route path="/app/payroll/:id" element={<PayrollRunScreen />} />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByRole('heading', { name: 'Liquidación 1' })).toBeInTheDocument()
    expect(screen.getByText(/Legajo 0001/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Editar' }))
    await user.click(screen.getByRole('button', { name: '+ Agregar haber' }))
    const lines = screen.getByRole('list', { name: 'Haberes y descuentos de Pérez, Juan' })
    const concepts = within(lines).getAllByLabelText('Concepto')
    const amounts = within(lines).getAllByLabelText('Importe')
    await user.type(concepts[2], 'Horas extra')
    await user.type(amounts[2], '20000')
    const discount = screen.getByLabelText('Bonificación %')
    await user.clear(discount)
    await user.type(discount, '10')
    expect(screen.getByText(/Se le bonifica .*5\.000,00 y se le descuenta .*45\.000,00 del sueldo\./)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Guardar recibo' }))

    await waitFor(() => expect(callsTo('PUT /payroll/runs/r-1/payslips/p-1')).toHaveLength(1))
    expect(bodyOf('PUT /payroll/runs/r-1/payslips/p-1')).toEqual({
      lines: [
        { kind: 'Earning', source: 'BaseSalary', concept: 'Sueldo básico', amount: 500000 },
        { kind: 'Deduction', source: 'Advances', concept: 'Adelantos y saldo pendiente', amount: 100000 },
        { kind: 'Earning', source: 'Manual', concept: 'Horas extra', amount: 20000 },
      ],
      purchasesDiscountPercent: 10,
    })

    await user.click(screen.getByRole('button', { name: 'Pagar liquidación' }))
    await waitFor(() => expect(screen.getByLabelText('Se paga desde')).toHaveValue('acc-cash'))
    await user.click(screen.getByRole('button', { name: 'Pagar' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Pagar' }))
    await waitFor(() => expect(callsTo('POST /payroll/runs/r-1/pay')).toHaveLength(1))
    expect(bodyOf('POST /payroll/runs/r-1/pay')).toMatchObject({ accountId: 'acc-cash' })
  })
})
