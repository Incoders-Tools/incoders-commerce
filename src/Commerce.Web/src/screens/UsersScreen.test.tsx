import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UsersScreen } from './UsersScreen'
import type { UserSummary } from '@/api/types'

const seller: UserSummary = {
  userId: 'user-1',
  email: 'staff@example.com',
  roleNames: ['seller'],
  isRevoked: false,
}

// T4b: a second record with different values in every asserted column, so the
// exclusion assertions below compare rows the screen really renders.
const revokedProvider: UserSummary = {
  userId: 'user-2',
  email: 'supplier@vendor.test',
  roleNames: ['provider'],
  isRevoked: true,
}

describe('UsersScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const listOnce = (users: UserSummary[]) =>
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(users), { status: 200 }))

  it('submits the administrator-entered replacement password for a forced reset', async () => {
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    await user.type(screen.getByLabelText('Replacement password for staff@example.com'), 'Unique-Password-42!')
    await user.click(screen.getByRole('button', { name: 'Force reset' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/reset-password')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ newPassword: 'Unique-Password-42!' })
  })

  it('refuses a forced reset with no replacement password and says why', async () => {
    listOnce([seller])

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    await user.click(screen.getByRole('button', { name: 'Force reset' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/replacement password/i)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('creates a user from the always-visible create form and refreshes the list', async () => {
    listOnce([])
      .mockResolvedValueOnce(new Response(JSON.stringify({ userId: 'user-2' }), { status: 201 }))
    listOnce([seller])

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('No users yet.')
    await user.type(screen.getByLabelText('User email'), 'staff@example.com')
    await user.type(screen.getByLabelText('User password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Create user' }))

    await screen.findByText('staff@example.com')
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      email: 'staff@example.com',
      password: 'correct-horse-battery-staple',
      roleNames: ['seller'],
      branchIds: [],
    })
  })

  it('saves the selected roles for a listed user', async () => {
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    await user.click(screen.getByRole('button', { name: 'Save roles' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/roles')
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
  })

  it('surfaces a load failure as an alert', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<UsersScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/unable to load users/i)
  })

  // ---- T4b: the shared data-view layer ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    listOnce([seller])

    const { container } = render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"]')).toBeNull()
  })

  it('renders the real user columns for each listed record', async () => {
    listOnce([revokedProvider])

    render(<UsersScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(within(row).getByText('provider')).toBeInTheDocument()
    expect(within(row).getByText('Revoked')).toBeInTheDocument()
  })

  it('shows an empty state when there are no users', async () => {
    listOnce([])

    render(<UsersScreen />)

    expect(await screen.findByText('No users yet.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('filters the listed users client-side by email or role', async () => {
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/search users/i), 'vendor')

    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(screen.queryByText('staff@example.com')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.clear(screen.getByLabelText(/search users/i))
    await user.type(screen.getByLabelText(/search users/i), 'provider')

    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(screen.queryByText('staff@example.com')).not.toBeInTheDocument()
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    await user.type(screen.getByLabelText(/search users/i), 'zzzz')

    expect(screen.getByText(/no users match/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    listOnce([seller, revokedProvider])
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    const first = render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /card view/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:users')).toBe('cards')

    first.unmount()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still forces a password reset from the card view', async () => {
    window.localStorage.setItem('view:users', 'cards')
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    // Prove we really are in the card layout, not just re-testing the table.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)

    await user.type(screen.getByLabelText('Replacement password for staff@example.com'), 'Unique-Password-42!')
    await user.click(screen.getByRole('button', { name: 'Force reset' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/reset-password')
  })
})
