import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AccountOverdueBanner, AccountSuspendedScreen } from './AccountStanding'

/** organization-account-standing polish (A11): the countdown and the suspended screen on their own. */
describe('AccountOverdueBanner', () => {
  beforeEach(() => vi.useFakeTimers({ toFake: ['Date'] }))
  afterEach(() => vi.useRealTimers())

  it('counts the days elapsed since the standing was received, so a wrong PC clock does not break the count', () => {
    // The PC clock is two years ahead; the server said 30 days when it was received (by that same clock) 3 days ago.
    vi.setSystemTime(new Date(2028, 9, 12, 10, 0, 0))

    render(<AccountOverdueBanner standing={{ status: 'Overdue', daysLeft: 30, suspendsOn: '2026-11-08', receivedOn: '2028-10-09' }} />)

    expect(screen.getByRole('status')).toHaveTextContent('se suspende en 27 días (el 08/11/2026)')
  })

  it('shows nothing for a date it cannot read, never NaN', () => {
    vi.setSystemTime(new Date(2026, 9, 9, 10, 0, 0))

    const { container } = render(<AccountOverdueBanner standing={{ status: 'Overdue', daysLeft: null, suspendsOn: 'not-a-date' }} />)

    expect(container).toBeEmptyDOMElement()
  })
})

describe('AccountSuspendedScreen', () => {
  it('stays on the suspended screen and lets the user try again when checking fails', async () => {
    const onCheckAgain = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce(undefined)
    const user = userEvent.setup()
    render(<AccountSuspendedScreen isAdmin={false} onCheckAgain={onCheckAgain} />)

    await user.click(screen.getByRole('button', { name: 'Volver a verificar' }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Volver a verificar' })).toBeEnabled())
    expect(screen.getByRole('heading', { name: 'Cuenta suspendida' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Volver a verificar' }))
    expect(onCheckAgain).toHaveBeenCalledTimes(2)
  })
})
