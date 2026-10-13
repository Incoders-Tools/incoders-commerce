import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PayrollRunDetail } from '@/api/employees'
import { PayslipsPrintScreen } from './PayslipsPrintScreen'

/** owner feedback P5: a printed payslip says from which accounts it was paid, and what stayed owed to the employee. */
describe('PayslipsPrintScreen', () => {
  const fetchMock = vi.fn()

  const paid: PayrollRunDetail = {
    run: {
      runId: 'r-1', runNumber: 1, branchId: 'b-1', periodFrom: '2026-10-01', periodTo: '2026-10-31', payFrequency: 'Monthly',
      status: 'Paid', notes: null, employeeCount: 1, totalNet: 350000, paidOn: '2026-10-31', paymentAccountId: 'acc-cash',
      createdAtUtc: '2026-10-31T00:00:00Z',
    },
    branchName: 'Arrecifes',
    paymentAccountName: 'Caja efectivo',
    payslips: [
      {
        payslipId: 'p-1', employeeId: 'e-1', fileNumber: 1, employeeName: 'Pérez, Juan', roleName: 'Carnicero', cuil: null, takesGoods: false,
        lines: [{ lineNo: 1, kind: 'Earning', source: 'BaseSalary', concept: 'Sueldo básico', amount: 350000 }],
        goodsItems: [], purchasesAmount: 0, purchasesDiscountPercent: 0, purchasesItemsDiscount: 0, purchasesDiscount: 0, purchasesDeducted: 0,
        earnings: 350000, deductions: 0, net: 350000, netPaid: 350000,
        payments: [
          { accountId: 'acc-cash', accountName: 'Caja efectivo', amount: 200000 },
          { accountId: 'acc-bank', accountName: 'Banco Nación', amount: 100000 },
        ],
        paid: 300000,
      },
    ],
  }

  beforeEach(() => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify(paid), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    vi.spyOn(window, 'print').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
    fetchMock.mockReset()
  })

  it('says from which accounts a payslip was paid and what stayed owed to the employee', async () => {
    render(
      <MemoryRouter initialEntries={['/print/payslips/r-1']}>
        <Routes>
          <Route path="/print/payslips/:id" element={<PayslipsPrintScreen />} />
        </Routes>
      </MemoryRouter>,
    )

    const paidLines = await screen.findAllByText(
      /Pagado el 31\/10\/2026: Caja efectivo .*200\.000,00 \+ Banco Nación .*100\.000,00\. Quedó a favor del empleado: .*50\.000,00\./,
    )
    expect(paidLines.length).toBeGreaterThan(0)
  })
})
