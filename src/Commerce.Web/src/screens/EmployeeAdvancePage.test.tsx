import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EmployeeAdvance, EmployeeRecord } from '@/api/employees'
import type { TreasuryAccount } from '@/api/treasury'
import { todayIso } from '@/lib/isoDate'
import { payPeriodStartIso } from '@/lib/payPeriod'
import { EmployeeAdvancePage } from './EmployeeAdvancePage'

/**
 * owner feedback 2026-10-14, A4: the advances of an employee, handed out and listed in one place. The history reads the
 * last month by default and any range on request; an advance still standing is corrected or voided whole (its cash and
 * the employee's account together, on the server).
 */
describe('EmployeeAdvancePage', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })
  const juan = { id: 'e-1', branchId: 'b-1', fullName: 'Bacca, Ariel', payFrequency: 'Monthly' } as EmployeeRecord
  const cash: TreasuryAccount = {
    accountId: 'acc-cash', branchId: 'b-1', branchName: 'Ruta 51', kind: 'Cash', name: 'Caja efectivo · Ruta 51',
    balance: 0, todayIn: 0, todayOut: 0, isActive: true,
  }
  const bank: TreasuryAccount = { ...cash, accountId: 'acc-bank', kind: 'Bank', name: 'Banco Nación' }
  const standing: EmployeeAdvance = {
    advanceId: 'adv-2', date: '2026-10-14', amount: 12000, accountId: 'acc-cash', accountName: 'Caja efectivo · Ruta 51',
    concept: 'a cuenta', isVoided: false, voidReason: null, voidedAtUtc: null, correctedByAdvanceId: null,
    correctsAdvanceId: null, createdAtUtc: '2026-10-14T15:57:42Z',
  }
  const voided: EmployeeAdvance = {
    ...standing, advanceId: 'adv-1', date: '2026-10-13', amount: 15000, concept: null, isVoided: true,
    voidReason: 'Anulado en la cuenta del empleado: Mal cargado', voidedAtUtc: '2026-10-14T02:36:37Z',
  }
  let advances: EmployeeAdvance[]

  const callsTo = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${String(url).split('?')[0]}` === key)
  const bodyOf = (key: string, index = 0) => JSON.parse(callsTo(key)[index][1].body as string)

  beforeEach(() => {
    advances = [standing, voided]
    vi.stubGlobal('fetch', fetchMock)
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url.split('?')[0]}`
      switch (key) {
        case 'GET /treasury/accounts':
          return json([cash, bank])
        case 'GET /employees/e-1/advances':
          return json(advances)
        case 'POST /employees/e-1/advances':
          return json({ advanceId: 'adv-3' })
        case 'POST /employees/e-1/advances/adv-2/void':
          return json(undefined, 204)
        case 'PUT /employees/e-1/advances/adv-2':
          return json({ advanceId: 'adv-4' })
        default:
          throw new TypeError(`unrouted ${key}`)
      }
    })
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const renderPage = (employee = juan) => render(<EmployeeAdvancePage employee={employee} onBack={() => {}} />)

  it('lists the advances of the current month for monthly staff, a voided one struck through with why, and reads another range', async () => {
    const user = userEvent.setup()
    renderPage()

    const history = await screen.findByRole('table', { name: 'Adelantos entregados' })
    const monthStart = `${todayIso().slice(0, 8)}01`
    expect(String(callsTo('GET /employees/e-1/advances')[0][0])).toBe(`/employees/e-1/advances?from=${monthStart}&to=${todayIso()}`)
    const rows = within(history).getAllByRole('row').slice(1)
    expect(within(rows[0]).getByText('14/10/2026')).toBeInTheDocument()
    expect(within(rows[0]).getByText('Vigente')).toBeInTheDocument()
    expect(within(rows[0]).getByText('a cuenta')).toBeInTheDocument()
    expect(within(rows[1]).getByText('Anulado')).toBeInTheDocument()
    expect(within(rows[1]).getByText('Anulado en la cuenta del empleado: Mal cargado')).toBeInTheDocument()
    expect(within(rows[1]).getByText(/15\.000,00/)).toHaveClass('line-through')
    expect(within(rows[1]).queryByRole('button')).not.toBeInTheDocument()
    expect(screen.getByText(/Total vigente en estas fechas: \$\s?12\.000,00/)).toBeInTheDocument()

    const from = screen.getByLabelText('Desde')
    await user.clear(from)
    await user.type(from, '01/01/2026')
    await waitFor(() =>
      expect(String(callsTo('GET /employees/e-1/advances').at(-1)![0])).toBe(`/employees/e-1/advances?from=2026-01-01&to=${todayIso()}`),
    )
  })

  it('voids an advance after asking why, and reads the history again', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: /^Anular el adelanto del 14\/10\/2026 por \$\s12\.000,00$/ }))
    const dialog = screen.getByRole('dialog')
    expect(within(dialog).getByText(/vuelve a la cuenta Caja efectivo · Ruta 51/)).toBeInTheDocument()
    await user.type(within(dialog).getByLabelText('Motivo'), 'No se entregó')
    await user.click(within(dialog).getByRole('button', { name: 'Anular adelanto' }))

    await waitFor(() => expect(callsTo('POST /employees/e-1/advances/adv-2/void')).toHaveLength(1))
    expect(bodyOf('POST /employees/e-1/advances/adv-2/void')).toEqual({ reason: 'No se entregó' })
    expect(await screen.findByText(/Adelanto del 14\/10\/2026 anulado/)).toBeInTheDocument()
    await waitFor(() => expect(callsTo('GET /employees/e-1/advances')).toHaveLength(2))
  })

  it('corrects an advance from the form, loaded with it and asking why', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: /^Corregir el adelanto del 14\/10\/2026 por \$\s12\.000,00$/ }))
    expect(screen.getByRole('heading', { name: 'Corregir el adelanto del 14/10/2026' })).toBeInTheDocument()
    expect(screen.getByLabelText('Importe')).toHaveValue('12000')
    expect(screen.getByLabelText('Motivo (opcional)')).toHaveValue('a cuenta')
    await user.clear(screen.getByLabelText('Importe'))
    await user.type(screen.getByLabelText('Importe'), '10000')
    await user.selectOptions(screen.getByLabelText('Sale de'), 'acc-bank')
    await user.click(screen.getByRole('button', { name: 'Guardar corrección' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Escribí por qué lo corregís.')
    expect(callsTo('PUT /employees/e-1/advances/adv-2')).toHaveLength(0)

    await user.type(screen.getByLabelText('Motivo de la corrección'), 'Era de 10.000')
    await user.click(screen.getByRole('button', { name: 'Guardar corrección' }))

    await waitFor(() => expect(callsTo('PUT /employees/e-1/advances/adv-2')).toHaveLength(1))
    expect(bodyOf('PUT /employees/e-1/advances/adv-2')).toEqual({
      amount: 10000, accountId: 'acc-bank', date: '2026-10-14', concept: 'a cuenta', reason: 'Era de 10.000',
    })
    expect(await screen.findByText(/Adelanto corregido/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Nuevo adelanto' })).toBeInTheDocument()
    expect(screen.getByLabelText('Importe')).toHaveValue('')
  })

  it('hands out an advance from the branch drawer and shows it in the history, staying on the page', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.type(await screen.findByLabelText('Importe'), '50000')
    await waitFor(() => expect(screen.getByLabelText('Sale de')).toHaveValue('acc-cash'))
    await user.click(screen.getByRole('button', { name: 'Registrar adelanto' }))

    await waitFor(() => expect(callsTo('POST /employees/e-1/advances')).toHaveLength(1))
    expect(bodyOf('POST /employees/e-1/advances')).toMatchObject({ amount: 50000, accountId: 'acc-cash' })
    expect(await screen.findByText(/Adelanto de \$\s?50\.000,00 a Bacca, Ariel registrado/)).toBeInTheDocument()
    expect(screen.getByLabelText('Importe')).toHaveValue('')
    await waitFor(() => expect(callsTo('GET /employees/e-1/advances')).toHaveLength(2))
  })

  // owner feedback 2026-10-15, B5: the history opens on the employee's pay period.
  it('opens the history on the last 7 days for weekly staff and the last 15 for fortnightly staff', async () => {
    const weekly = renderPage({ ...juan, payFrequency: 'Weekly' })
    await screen.findByRole('table', { name: 'Adelantos entregados' })
    expect(screen.getByLabelText('Desde')).toHaveValue(payPeriodStartIso('Weekly', todayIso()).split('-').reverse().join('/'))
    expect(String(callsTo('GET /employees/e-1/advances')[0][0])).toContain(`from=${payPeriodStartIso('Weekly', todayIso())}&`)
    weekly.unmount()

    renderPage({ ...juan, payFrequency: 'Biweekly' })
    await screen.findByRole('table', { name: 'Adelantos entregados' })
    expect(String(callsTo('GET /employees/e-1/advances')[1][0])).toContain(`from=${payPeriodStartIso('Biweekly', todayIso())}&`)
  })

  // Follow-up F4 (review R3-history-load-race, R3-invalid-range-stale-list).
  it('shows the history of the latest dates asked, never a late answer for earlier ones', async () => {
    const original = fetchMock.getMockImplementation()!
    let answerFirst: (r: Response) => void = () => {}
    const first = new Promise<Response>((resolve) => (answerFirst = resolve))
    let reads = 0
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (!url.startsWith('/employees/e-1/advances')) return original(url, init)
      reads += 1
      return reads === 1 ? first : json([voided])
    })
    const user = userEvent.setup()
    renderPage()

    const from = await screen.findByLabelText('Desde')
    await user.clear(from)
    await user.type(from, '01/01/2026')
    expect(await screen.findByText('Anulado en la cuenta del empleado: Mal cargado')).toBeInTheDocument()
    answerFirst(json([standing]))
    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(screen.queryByText('a cuenta')).not.toBeInTheDocument()
    expect(screen.getByText('Anulado en la cuenta del empleado: Mal cargado')).toBeInTheDocument()

    // A range the other way round says so, without the list of the dates it no longer shows.
    const to = screen.getByLabelText('Hasta')
    await user.clear(to)
    await user.type(to, '01/12/2025')
    expect(await screen.findByText(/La fecha Desde tiene que ser anterior/)).toBeInTheDocument()
    expect(screen.queryByRole('table', { name: 'Adelantos entregados' })).not.toBeInTheDocument()
  })

  it('explains why an advance in a draft payroll is not voided', async () => {
    const original = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) =>
      url.endsWith('/void') ? json({ error: 'advance-in-payroll-draft' }, 409) : original(url, init),
    )
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: /Anular el adelanto del 14\/10\/2026/ }))
    await user.type(screen.getByLabelText('Motivo'), 'x')
    await user.click(screen.getByRole('button', { name: 'Anular adelanto' }))

    expect(await screen.findByText(/está en una liquidación en borrador/)).toBeInTheDocument()
  })
})
