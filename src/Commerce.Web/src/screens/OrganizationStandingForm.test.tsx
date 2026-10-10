import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationStandingForm } from './OrganizationStandingForm'
import type { OrganizationAccountStanding, OrganizationSummary } from '@/api/types'

const vacaVerde: OrganizationSummary = {
  id: '22222222-2222-2222-2222-222222222222',
  name: 'Vaca Verde',
  createdAt: '2024-02-15T00:00:00Z',
}

const overdue: OrganizationAccountStanding = {
  dueOn: '2026-10-08', graceDays: 30, suspendedAt: null, status: 'Overdue', suspendsOn: '2026-11-08', daysLeft: 30,
}
const suspendedByHand: OrganizationAccountStanding = {
  dueOn: '2026-10-08', graceDays: 30, suspendedAt: '2026-10-09T13:00:00Z', status: 'Suspended', suspendsOn: null, daysLeft: null,
}

/**
 * organization-account-standing T6: the system administrator sets the billing due date and grace days of an
 * organization (also how a payment is recorded: the next period's due date), suspends it by hand, and reactivates it.
 * Today is pinned to 2026-10-09, the ODD's example day.
 */
describe('OrganizationStandingForm', () => {
  const fetchMock = vi.fn()
  const onSaved = vi.fn()
  const onCancel = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2026, 9, 9, 10, 0, 0))
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    onSaved.mockReset()
    onCancel.mockReset()
  })

  const respond = (body: unknown, status = 200) =>
    fetchMock.mockResolvedValueOnce(new Response(body === undefined ? null : JSON.stringify(body), { status }))
  const sentBody = (call: number) => JSON.parse(fetchMock.mock.calls[call][1].body as string)
  const renderForm = () => render(<OrganizationStandingForm organization={vacaVerde} onSaved={onSaved} onCancel={onCancel} />)

  it('loads the standing and prefills the due date and grace days, showing where it stands', async () => {
    respond(overdue)

    renderForm()

    expect(await screen.findByLabelText('Vencimiento')).toHaveValue('2026-10-08')
    expect(screen.getByLabelText('Días de tolerancia')).toHaveValue(30)
    expect(screen.getByText('Vencida · se suspende el 08/11/2026 (30 días)')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe(`/account/organizations/${vacaVerde.id}/standing`)
  })

  it('records a payment by moving the due date, with the grace days', async () => {
    respond(overdue)
    respond(undefined, 204)
    const user = userEvent.setup()
    renderForm()

    const dueOn = await screen.findByLabelText('Vencimiento')
    await user.clear(dueOn)
    await user.type(dueOn, '2026-11-10')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1))
    expect(fetchMock.mock.calls[1][0]).toBe(`/account/organizations/${vacaVerde.id}/standing`)
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
    expect(sentBody(1)).toEqual({ dueOn: '2026-11-10', graceDays: 30 })
  })

  it('stops tracking billing when the due date is cleared', async () => {
    respond(overdue)
    respond(undefined, 204)
    const user = userEvent.setup()
    renderForm()

    await user.clear(await screen.findByLabelText('Vencimiento'))
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sentBody(1)).toEqual({ dueOn: null, graceDays: 30 })
  })

  it('rejects grace days outside 0 to 90 without calling the API', async () => {
    respond(overdue)
    const user = userEvent.setup()
    renderForm()

    const grace = await screen.findByLabelText('Días de tolerancia')
    await user.clear(grace)
    await user.type(grace, '91')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Los días de tolerancia tienen que ser un número entero de 0 a 90.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('suspends now only after confirming, then shows the organization as suspended', async () => {
    respond(overdue)
    respond(undefined, 204)
    respond(suspendedByHand)
    const user = userEvent.setup()
    renderForm()

    await user.click(await screen.findByRole('button', { name: 'Suspender ahora' }))
    expect(screen.getByRole('dialog')).toHaveTextContent('Vaca Verde')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    await user.click(screen.getByRole('button', { name: 'Suspender' }))

    expect(await screen.findByRole('button', { name: 'Reactivar' })).toBeInTheDocument()
    expect(fetchMock.mock.calls[1][0]).toBe(`/account/organizations/${vacaVerde.id}/standing/suspend`)
    expect(fetchMock.mock.calls[1][1].method).toBe('POST')
    expect(screen.getByText('Suspendida manualmente el 09/10/2026')).toBeInTheDocument()
  })

  it('does not reactivate with a due date before today', async () => {
    respond(suspendedByHand)
    const user = userEvent.setup()
    renderForm()

    await screen.findByRole('button', { name: 'Reactivar' })
    await user.click(screen.getByRole('button', { name: 'Reactivar' }))

    expect(await screen.findByText('Para reactivar, el vencimiento tiene que ser hoy o una fecha posterior.')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('reactivates with the next due date and the grace days', async () => {
    respond(suspendedByHand)
    respond(undefined, 204)
    const user = userEvent.setup()
    renderForm()

    const dueOn = await screen.findByLabelText('Vencimiento')
    await user.clear(dueOn)
    await user.type(dueOn, '2026-11-09')
    await user.click(screen.getByRole('button', { name: 'Reactivar' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1))
    expect(fetchMock.mock.calls[1][0]).toBe(`/account/organizations/${vacaVerde.id}/standing/reactivate`)
    expect(sentBody(1)).toEqual({ dueOn: '2026-11-09', graceDays: 30 })
  })

  it('keeps the edits being typed when suspending, instead of reloading over them', async () => {
    respond(overdue)
    respond(undefined, 204)
    respond(suspendedByHand)
    const user = userEvent.setup()
    renderForm()

    const dueOn = await screen.findByLabelText('Vencimiento')
    await user.clear(dueOn)
    await user.type(dueOn, '2026-11-20')
    await user.click(screen.getByRole('button', { name: 'Suspender ahora' }))
    await user.click(screen.getByRole('button', { name: 'Suspender' }))

    await screen.findByRole('button', { name: 'Reactivar' })
    expect(screen.getByLabelText('Vencimiento')).toHaveValue('2026-11-20')
  })

  it('does not offer to suspend an organization its dates already suspended', async () => {
    respond({ dueOn: '2026-08-01', graceDays: 30, suspendedAt: null, status: 'Suspended', suspendsOn: '2026-09-01', daysLeft: null })

    renderForm()

    await screen.findByText('Suspendida desde el 01/09/2026')
    expect(screen.queryByRole('button', { name: 'Suspender ahora' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeInTheDocument()
  })

  it('shows the manual suspension date on the business day (Buenos Aires), not the browser time zone', async () => {
    // 02:00 UTC on the 10th is still the 9th, 23:00, in Buenos Aires. On a machine set to Argentina's time this cannot
    // tell the business zone from the browser's (changing TZ has no effect on Windows' Node); `lib/businessDate.test.ts`
    // is the discriminating test, with an explicit zone.
    respond({ ...suspendedByHand, suspendedAt: '2026-10-10T02:00:00Z' })

    renderForm()

    expect(await screen.findByText('Suspendida manualmente el 09/10/2026')).toBeInTheDocument()
  })

  it('says the organization was suspended even when re-reading its standing fails afterwards', async () => {
    respond(overdue)
    respond(undefined, 204)
    respond({ title: 'boom' }, 500)
    const user = userEvent.setup()
    renderForm()

    await user.click(await screen.findByRole('button', { name: 'Suspender ahora' }))
    await user.click(screen.getByRole('button', { name: 'Suspender' }))

    expect(await screen.findByText('La organización quedó suspendida, pero no se pudo recargar su estado.')).toBeInTheDocument()
    expect(screen.queryByText('No se pudo guardar el estado de cuenta.')).not.toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeInTheDocument()
  })
})
