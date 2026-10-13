import { render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeRecord } from '@/api/employees'
import type { AccountStatement, AccountSummary } from '@/api/types'
import { EmployeeAccountScreen } from './SupplierAccountScreen'

/**
 * owner feedback P2: the employee's account reads its position, not due dates: what its account holds (advances), the
 * goods its linked customer owes, the overtime not paid yet, and the position to date without the month's salary.
 */
describe('EmployeeAccountScreen', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  const ariel = {
    id: 'e-1', branchId: 'b-1', branchName: 'Ruta 51', fileNumber: 1, firstName: 'Ariel', lastName: 'Bacca', fullName: 'Bacca, Ariel',
    documentNumber: null, cuil: null, roleId: null, roleName: null, phone: null, email: null, address: null, hireDate: '2026-06-15',
    terminationDate: null, payFrequency: 'Monthly', baseSalary: 1200000, customerId: 'c-1', notes: null, isActive: true,
    createdAtUtc: '2026-10-13T00:00:00Z', updatedAtUtc: '2026-10-13T00:00:00Z', balance: -15000, purchasesOwed: 36000,
    overtimeHourlyRate: 9000, terminationReason: null, overtimeToPay: 40500, overtimeHoursToPay: 4.5,
  } as EmployeeRecord

  const summary: AccountSummary = {
    asOf: '2026-10-13', balance: -15000, overdue: 0, current: 0, aging: { d0_30: 0, d31_60: 0, d61_90: 0, d90plus: 0 },
  }
  const statement: AccountStatement = { openingBalance: 0, closingBalance: -15000, movements: [] }

  let employee: EmployeeRecord

  beforeEach(() => {
    employee = ariel
    fetchMock.mockImplementation(async (url: string) => {
      const path = url.split('?')[0]
      if (path === '/employees/e-1') return json(employee)
      if (path === '/employees/e-1/account/summary') return json(summary)
      if (path === '/employees/e-1/account/statement') return json(statement)
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
      <MemoryRouter initialEntries={['/app/employees/e-1/account']}>
        <Routes>
          <Route path="/app/employees/:id/account" element={<EmployeeAccountScreen />} />
        </Routes>
      </MemoryRouter>,
    )

  it('reads the advances, the goods, the overtime to pay and the position to date, and no due dates', async () => {
    renderScreen()

    const account = await screen.findByTestId('position-account')
    expect(account).toHaveTextContent('Saldo de la cuenta')
    expect(account).toHaveTextContent(/15\.000,00/)
    expect(account).toHaveTextContent('Nos debe (adelantos y saldos)')
    expect(screen.getByTestId('position-goods')).toHaveTextContent(/36\.000,00.*Nos debe \(mercadería a descontar\)/)
    expect(screen.getByTestId('position-overtime')).toHaveTextContent(/40\.500,00.*Le debemos \(4,5 h sin liquidar\)/)
    // -15.000 - 36.000 + 40.500 = -10.500: it owes 10.500.
    const total = screen.getByTestId('position-total')
    expect(total).toHaveTextContent('Posición a hoy')
    expect(within(total).getByText(/10\.500,00/)).toBeInTheDocument()
    expect(total).toHaveTextContent('Nos debe')
    expect(total).toHaveTextContent('Sin contar el sueldo del mes, que se suma al liquidar.')

    expect(screen.queryByText('Vencido')).not.toBeInTheDocument()
    expect(screen.queryByText('Antigüedad de lo vencido')).not.toBeInTheDocument()
  })

  it('says when the employee takes no goods, and an even position as such', async () => {
    employee = { ...ariel, customerId: null, purchasesOwed: 0, balance: 0, overtimeToPay: 0, overtimeHoursToPay: 0 }
    renderScreen()

    expect(await screen.findByTestId('position-goods')).toHaveTextContent('No lleva mercadería')
    expect(screen.getByTestId('position-total')).toHaveTextContent('Sin saldo')
  })
})
