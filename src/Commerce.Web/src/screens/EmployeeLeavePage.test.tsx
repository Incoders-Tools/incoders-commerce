import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeRecord } from '@/api/employees'
import { EmployeeLeavePage } from './EmployeeLeavePage'

/**
 * employee-lifecycle E3: "Gestionar baja". An active employee is given the leave with its date and reason; one given
 * the leave shows them, and offers to settle its last salary (a payroll draft, reviewed and paid in "Sueldos") or to
 * reinstate it.
 */
describe('EmployeeLeavePage', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const juan = {
    id: 'e-1', branchId: 'b-1', branchName: 'Arrecifes', fileNumber: 1, firstName: 'Juan', lastName: 'Pérez', fullName: 'Pérez, Juan',
    documentNumber: null, cuil: null, roleId: null, roleName: null, phone: null, email: null, address: null, hireDate: '2025-01-01',
    terminationDate: null, terminationReason: null, payFrequency: 'Monthly', baseSalary: 600000, customerId: null, notes: null, isActive: true,
    createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', balance: 0, purchasesOwed: 0, overtimeHourlyRate: null,
  } as EmployeeRecord
  const left = { ...juan, isActive: false, terminationDate: '2026-10-10', terminationReason: 'Renuncia' } as EmployeeRecord

  let routes: Record<string, (init?: RequestInit) => Response>

  beforeEach(() => {
    routes = {}
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (!routes[key]) throw new TypeError(`unrouted ${key}`)
      return routes[key](init)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const calls = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${url}` === key)
  const bodyOf = (key: string) => JSON.parse(calls(key)[0][1].body as string)

  const renderPage = (employee: EmployeeRecord, onReinstated = vi.fn()) => {
    render(
      <MemoryRouter initialEntries={['/app/employees']}>
        <Routes>
          <Route path="/app/employees" element={<EmployeeLeavePage employee={employee} onBack={vi.fn()} onReinstated={onReinstated} />} />
          <Route path="/app/payroll/:id" element={<p>Liquidación abierta</p>} />
        </Routes>
      </MemoryRouter>,
    )
    return onReinstated
  }

  it('gives the leave with its date and reason, then offers to settle the last salary', async () => {
    routes['POST /employees/e-1/active'] = () => json(undefined, 204)
    routes['GET /employees/e-1'] = () => json(left)
    const user = userEvent.setup()
    renderPage(juan)

    expect(screen.getByRole('heading', { name: 'Baja de Pérez, Juan' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Fecha de baja'), { target: { value: '2026-10-10' } })
    await user.type(screen.getByLabelText('Motivo'), 'Renuncia')
    await user.click(screen.getByRole('button', { name: 'Dar de baja' }))

    await waitFor(() => expect(calls('POST /employees/e-1/active')).toHaveLength(1))
    expect(bodyOf('POST /employees/e-1/active')).toEqual({ isActive: false, terminationDate: '2026-10-10', terminationReason: 'Renuncia' })
    expect(await screen.findByText('Dado de baja el 10/10/2026. Motivo: Renuncia.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Liquidar último sueldo' })).toBeInTheDocument()
  })

  it('shows the leave it just recorded even when re-reading the employee fails', async () => {
    routes['POST /employees/e-1/active'] = () => json(undefined, 204)
    routes['GET /employees/e-1'] = () => json({ error: 'boom' }, 500)
    const user = userEvent.setup()
    renderPage(juan)

    fireEvent.change(screen.getByLabelText('Fecha de baja'), { target: { value: '2026-10-10' } })
    await user.type(screen.getByLabelText('Motivo'), 'Renuncia')
    await user.click(screen.getByRole('button', { name: 'Dar de baja' }))

    expect(await screen.findByText('Dado de baja el 10/10/2026. Motivo: Renuncia.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Dar de baja' })).not.toBeInTheDocument()
  })

  it('says the employee was given the leave even without a date or reason (an older terminal)', () => {
    renderPage({ ...juan, isActive: false })
    expect(screen.getByRole('status')).toHaveTextContent('Dado de baja.')
  })

  it('asks for the reason and refuses a day after today, without calling the server', async () => {
    const user = userEvent.setup()
    renderPage(juan)

    await user.click(screen.getByRole('button', { name: 'Dar de baja' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Escribí el motivo de la baja.')

    await user.type(screen.getByLabelText('Motivo'), 'Renuncia')
    fireEvent.change(screen.getByLabelText('Fecha de baja'), { target: { value: '2099-01-01' } })
    await user.click(screen.getByRole('button', { name: 'Dar de baja' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Elegí un día que ya pasó (o hoy).')
    expect(calls('POST /employees/e-1/active')).toHaveLength(0)
  })

  it('settles the last salary of an employee given the leave by opening its payroll draft', async () => {
    routes['POST /payroll/runs/final-settlement'] = () => json({ runId: 'r-9' }, 201)
    const user = userEvent.setup()
    renderPage(left)

    expect(screen.getByText('Dado de baja el 10/10/2026. Motivo: Renuncia.')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Liquidar último sueldo' }))

    expect(await screen.findByText('Liquidación abierta')).toBeInTheDocument()
    expect(bodyOf('POST /payroll/runs/final-settlement')).toEqual({ employeeId: 'e-1' })
  })

  it('explains why the last salary cannot be settled', async () => {
    routes['POST /payroll/runs/final-settlement'] = () => json({ error: 'employee-other-branch' }, 409)
    const user = userEvent.setup()
    renderPage(left)

    await user.click(screen.getByRole('button', { name: 'Liquidar último sueldo' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Elegí la sucursal del empleado en la barra superior para liquidarlo.')
  })

  it('reinstates an employee given the leave, or explains that a draft must go first', async () => {
    let answer = json({ error: 'employee-in-payroll-draft' }, 409)
    routes['POST /employees/e-1/active'] = () => answer
    const user = userEvent.setup()
    const onReinstated = renderPage(left)

    await user.click(screen.getByRole('button', { name: 'Reincorporar' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Tiene una liquidación en borrador: pagala o descartala antes de reincorporarlo.')
    expect(onReinstated).not.toHaveBeenCalled()

    answer = json(undefined, 204)
    await user.click(screen.getByRole('button', { name: 'Reincorporar' }))
    await waitFor(() => expect(onReinstated).toHaveBeenCalledTimes(1))
    expect(bodyOf('POST /employees/e-1/active')).toEqual({ isActive: true })
  })
})
