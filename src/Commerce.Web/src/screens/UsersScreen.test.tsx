import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UsersScreen } from './UsersScreen'

describe('UsersScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('submits the administrator-entered replacement password for a forced reset', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([
        { userId: 'user-1', email: 'staff@example.com', roleNames: ['seller'], isRevoked: false },
      ]), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<UsersScreen />)

    await screen.findByText('staff@example.com')
    await user.type(screen.getByLabelText('Replacement password for staff@example.com'), 'Unique-Password-42!')
    await user.click(screen.getByRole('button', { name: 'Force reset' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/reset-password')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ newPassword: 'Unique-Password-42!' })
  })
})
