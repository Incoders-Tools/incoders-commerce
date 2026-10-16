import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeRecord, OvertimeRecord } from '@/api/employees'
import { EmployeeOvertimePage } from './EmployeeOvertimePage'

/**
 * employee-lifecycle E2: the overtime of an employee, recorded when it happens. A Sunday proposes x2, any other day x1;
 * the payroll takes the pending ones by itself, and only a pending one can be deleted.
 */
describe('EmployeeOvertimePage', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const juan = {
    id: 'e-1', branchId: 'b-1', branchName: 'Arrecifes', fileNumber: 1, firstName: 'Juan', lastName: 'Pérez', fullName: 'Pérez, Juan',
    documentNumber: null, cuil: null, roleId: null, roleName: null, phone: null, email: null, address: null, hireDate: null,
    terminationDate: null, payFrequency: 'Monthly', baseSalary: 500000, customerId: null, notes: null, isActive: true,
    createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', balance: 0, purchasesOwed: 0, overtimeHourlyRate: 2000,
  } as EmployeeRecord

  const record = (overrides: Partial<OvertimeRecord> = {}): OvertimeRecord => ({
    id: 'o-1', employeeId: 'e-1', workDate: '2026-10-11', hours: 4, multiplier: 2, hourlyRate: 2000, amount: 16000, note: 'Inventario',
    status: 'Pending', runNumber: null, createdAtUtc: '2026-10-11T20:00:00Z', ...overrides,
  })

  let stored: OvertimeRecord[]

  beforeEach(() => {
    stored = []
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /employees/e-1/overtime') return json(stored)
      if (key === 'POST /employees/e-1/overtime') {
        const created = record({ id: 'o-new', ...JSON.parse(init!.body as string), amount: 16000 })
        stored = [created, ...stored]
        return json(created, 201)
      }
      if (key.startsWith('DELETE /employees/e-1/overtime/')) {
        stored = stored.filter((item) => !key.endsWith(item.id))
        return json(undefined, 204)
      }
      throw new TypeError(`unrouted ${key}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const calls = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${url}` === key)

  it('records overtime proposing x2 on a Sunday, previews its amount and lists it as pending', async () => {
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={juan} onBack={vi.fn()} />)

    expect(await screen.findByText(/No tiene horas extra cargadas/)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2026-10-11' } })
    expect(screen.getByLabelText('Multiplicador')).toHaveValue('2')
    await user.type(screen.getByLabelText('Horas'), '4')
    await user.type(screen.getByLabelText('Nota (opcional)'), 'Inventario')
    expect(screen.getByText(/Importe: \$\s16\.000,00/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Registrar horas extra' }))

    await waitFor(() => expect(calls('POST /employees/e-1/overtime')).toHaveLength(1))
    expect(JSON.parse(calls('POST /employees/e-1/overtime')[0][1].body as string)).toEqual({
      workDate: '2026-10-11', hours: 4, multiplier: 2, note: 'Inventario',
    })
    const row = (await screen.findByText('Pendiente')).closest('tr')!
    expect(within(row).getByText('11/10/2026')).toBeInTheDocument()
    expect(within(row).getByText(/16\.000,00/)).toBeInTheDocument()
  })

  it('proposes x1 on a weekday, and the multiplier can still be changed', async () => {
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={juan} onBack={vi.fn()} />)
    await screen.findByText(/No tiene horas extra cargadas/)

    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2026-10-05' } })
    expect(screen.getByLabelText('Multiplicador')).toHaveValue('1')
    await user.clear(screen.getByLabelText('Multiplicador'))
    await user.type(screen.getByLabelText('Multiplicador'), '1,5')
    await user.type(screen.getByLabelText('Horas'), '2,5')
    await user.click(screen.getByRole('button', { name: 'Registrar horas extra' }))

    await waitFor(() => expect(calls('POST /employees/e-1/overtime')).toHaveLength(1))
    expect(JSON.parse(calls('POST /employees/e-1/overtime')[0][1].body as string)).toMatchObject({ workDate: '2026-10-05', hours: 2.5, multiplier: 1.5 })
  })

  it('refuses hours out of range without calling the server', async () => {
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={juan} onBack={vi.fn()} />)
    await screen.findByText(/No tiene horas extra cargadas/)

    for (const text of ['25', '0']) {
      await user.clear(screen.getByLabelText('Horas'))
      await user.type(screen.getByLabelText('Horas'), text)
      await user.click(screen.getByRole('button', { name: 'Registrar horas extra' }))
      expect(screen.getByRole('alert')).toHaveTextContent('Escribí las horas: más de 0 y hasta 24, con hasta dos decimales.')
    }
    expect(calls('POST /employees/e-1/overtime')).toHaveLength(0)
  })

  it('refuses a day after today: overtime is recorded once worked', async () => {
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={juan} onBack={vi.fn()} />)
    await screen.findByText(/No tiene horas extra cargadas/)

    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2099-01-01' } })
    await user.type(screen.getByLabelText('Horas'), '2')
    await user.click(screen.getByRole('button', { name: 'Registrar horas extra' }))

    expect(screen.getByRole('alert')).toHaveTextContent('Elegí un día que ya pasó (o hoy).')
    expect(calls('POST /employees/e-1/overtime')).toHaveLength(0)
  })

  it('without an overtime rate explains where to set it and offers no form', async () => {
    render(<EmployeeOvertimePage employee={{ ...juan, overtimeHourlyRate: null }} onBack={vi.fn()} />)

    expect(await screen.findByText(/Cargá el valor de la hora extra en los datos del empleado/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Registrar horas extra' })).not.toBeInTheDocument()
  })

  it('deletes only a pending record, after confirming, and shows where the others went', async () => {
    stored = [
      record(),
      record({ id: 'o-2', workDate: '2026-10-05', status: 'InPayroll', runNumber: 3 }),
      record({ id: 'o-3', workDate: '2026-09-20', status: 'Paid', runNumber: 2 }),
    ]
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={juan} onBack={vi.fn()} />)

    expect(await screen.findByText('En la liquidación 3')).toBeInTheDocument()
    expect(screen.getByText('Liquidada (liquidación 2)')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /^Borrar/ })).toHaveLength(1)

    await user.click(screen.getByRole('button', { name: 'Borrar horas extra del 11/10/2026' }))
    const dialog = screen.getByRole('dialog')
    await user.click(within(dialog).getByRole('button', { name: 'Borrar' }))

    await waitFor(() => expect(calls('DELETE /employees/e-1/overtime/o-1')).toHaveLength(1))
    await waitFor(() => expect(calls('GET /employees/e-1/overtime')).toHaveLength(2))
    const rows = within(await screen.findByRole('table')).getAllByRole('row')
    expect(rows).toHaveLength(3) // the header and the two a payroll took
    expect(screen.getByText('En la liquidación 3')).toBeInTheDocument()
    expect(screen.queryByText('Pendiente')).not.toBeInTheDocument()
  })

  it('shows why a delete was refused even when the employee has no rate any more', async () => {
    stored = [record()]
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /employees/e-1/overtime') return json(stored)
      if (key === 'DELETE /employees/e-1/overtime/o-1') return json({ error: 'overtime-settled' }, 409)
      throw new TypeError(`unrouted ${key}`)
    })
    const user = userEvent.setup()
    render(<EmployeeOvertimePage employee={{ ...juan, overtimeHourlyRate: null }} onBack={vi.fn()} />)

    await user.click(await screen.findByRole('button', { name: 'Borrar horas extra del 11/10/2026' }))
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Borrar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Esas horas extra ya están en una liquidación: se cambian ahí.')
  })
})
