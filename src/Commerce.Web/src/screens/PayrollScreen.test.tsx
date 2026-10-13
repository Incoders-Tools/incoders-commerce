import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeRecord } from '@/api/employees'
import { PayrollScreen } from './PayrollScreen'

/**
 * owner feedback P4: a payroll is prepared for the employees chosen: the active ones of the frequency, all checked at
 * first; at least one.
 */
describe('PayrollScreen', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const employee = (id: string, fullName: string, fileNumber: number, payFrequency = 'Monthly') =>
    ({ id, fullName, fileNumber, payFrequency, isActive: true }) as EmployeeRecord

  beforeEach(() => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`
      if (key === 'GET /payroll/runs') return json([])
      if (key === 'GET /employees') {
        return json([employee('e-1', 'Bacca, Ariel', 1), employee('e-2', 'Gómez, Ana', 2), employee('e-3', 'Ruiz, Beto', 3, 'Weekly')])
      }
      if (key === 'POST /payroll/runs') return json({ runId: 'r-9' }, 201)
      throw new TypeError(`unrouted ${key}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const calls = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)

  function Location() {
    return <output data-testid="location">{useLocation().pathname}</output>
  }

  const renderScreen = () =>
    render(
      <MemoryRouter initialEntries={['/app/payroll']}>
        <Routes>
          <Route path="/app/payroll" element={<PayrollScreen />} />
          <Route path="*" element={<Location />} />
        </Routes>
      </MemoryRouter>,
    )

  it('prepares the run for the employees chosen among the active ones of the frequency, all checked at first', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Nueva liquidación' }))
    const chosen = await screen.findByRole('group', { name: 'Empleados a liquidar' })
    await waitFor(() => expect(within(chosen).getByLabelText('0001 · Bacca, Ariel')).toBeChecked())
    expect(within(chosen).getByLabelText('0002 · Gómez, Ana')).toBeChecked()
    expect(within(chosen).queryByLabelText('0003 · Ruiz, Beto')).not.toBeInTheDocument() // paid weekly
    expect(within(chosen).getByText('2 de 2 elegidos')).toBeInTheDocument()

    await user.click(within(chosen).getByLabelText('0002 · Gómez, Ana'))
    expect(within(chosen).getByText('1 de 2 elegidos')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Preparar liquidación' }))

    await waitFor(() => expect(calls('POST /payroll/runs')).toHaveLength(1))
    expect(JSON.parse(calls('POST /payroll/runs')[0][1].body as string)).toMatchObject({ payFrequency: 'Monthly', employeeIds: ['e-1'] })
    expect(await screen.findByTestId('location')).toHaveTextContent('/app/payroll/r-9')
  })

  it('offers the employees of the frequency chosen, and asks for at least one', async () => {
    const user = userEvent.setup()
    renderScreen()

    await user.click(await screen.findByRole('button', { name: 'Nueva liquidación' }))
    await user.selectOptions(screen.getByLabelText('Empleados que cobran'), 'Weekly')
    const chosen = screen.getByRole('group', { name: 'Empleados a liquidar' })
    expect(await within(chosen).findByLabelText('0003 · Ruiz, Beto')).toBeChecked()
    expect(within(chosen).queryByLabelText('0001 · Bacca, Ariel')).not.toBeInTheDocument()

    await user.click(within(chosen).getByRole('button', { name: 'Ninguno' }))
    await user.click(screen.getByRole('button', { name: 'Preparar liquidación' }))
    expect(await screen.findByText('Elegí al menos un empleado.')).toBeInTheDocument()
    expect(calls('POST /payroll/runs')).toHaveLength(0)

    await user.click(within(chosen).getByRole('button', { name: 'Todos' }))
    expect(within(chosen).getByLabelText('0003 · Ruiz, Beto')).toBeChecked()
  })
})
