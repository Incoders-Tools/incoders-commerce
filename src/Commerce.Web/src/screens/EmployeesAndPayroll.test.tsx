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
  }
  const cash: TreasuryAccount = {
    accountId: 'acc-cash', branchId: 'b-1', branchName: 'Arrecifes', kind: 'Cash', name: 'Caja efectivo · Arrecifes',
    balance: 0, todayIn: 0, todayOut: 0, isActive: true,
  }

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
      'POST /employees': () => json({ ...juan, id: 'e-2', firstName: 'Ana', fullName: 'Gómez, Ana', fileNumber: 2 }, 201),
    })
    const user = userEvent.setup()
    renderEmployees()

    expect(await screen.findByText('Pérez, Juan')).toBeInTheDocument()
    expect(screen.getByText('Nos debe $ 100.000,00'.replace(/ /g, ' '))).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Nuevo empleado' }))
    await user.type(screen.getByLabelText('Apellido'), 'Gómez')
    await user.type(screen.getByLabelText('Nombre'), 'Ana')
    await user.type(screen.getByLabelText('Sueldo pactado por período'), '650000')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(callsTo('POST /employees')).toHaveLength(1))
    expect(bodyOf('POST /employees')).toMatchObject({
      branchId: 'b-1', firstName: 'Ana', lastName: 'Gómez', payFrequency: 'Monthly', baseSalary: 650000, takesGoods: true,
    })
    expect(await screen.findByText('Gómez, Ana guardado (legajo 2).')).toBeInTheDocument()
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
        purchasesAmount: 50000, purchasesDiscountPercent: 0, purchasesDiscount: 0, purchasesDeducted: 50000,
        earnings: 500000, deductions: 100000, net: 350000, netPaid: 350000,
      },
    ],
  }

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
