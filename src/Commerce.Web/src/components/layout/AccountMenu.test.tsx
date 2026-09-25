import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router'
import { AuthContext } from '@/auth/AuthContext'
import { ThemeProvider } from '@/theme/ThemeProvider'
import type { SignedInResponse } from '@/api/types'
import { AccountMenu } from './AccountMenu'

const baseUser: SignedInResponse = {
  organizationId: 'org-1',
  userId: 'user-1',
  displayName: 'Ada Lovelace',
  permissions: 0,
  isSystemAdmin: false,
}

function renderMenu(signOut = vi.fn().mockResolvedValue(undefined)) {
  render(
    <MemoryRouter>
      <AuthContext.Provider value={{ user: baseUser, error: null, signIn: vi.fn(), signOut }}>
        <ThemeProvider>
          <AccountMenu />
        </ThemeProvider>
      </AuthContext.Provider>
    </MemoryRouter>,
  )
  return { signOut }
}

describe('AccountMenu', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.classList.remove('dark')
  })

  afterEach(() => {
    cleanup()
    document.documentElement.classList.remove('dark')
  })

  it('shows the trigger with the user display name and a closed menu by default', () => {
    renderMenu()

    expect(screen.getByRole('button', { name: /ada lovelace/i })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('opens the menu on trigger click and shows the display name, change-password link, and theme switcher', async () => {
    const user = userEvent.setup()
    renderMenu()

    await user.click(screen.getByRole('button', { name: /ada lovelace/i }))

    const menu = screen.getByRole('menu')
    expect(within(menu).getByText('Ada Lovelace')).toBeInTheDocument()

    const changePasswordLink = within(menu).getByRole('menuitem', { name: /change password/i })
    expect(changePasswordLink).toHaveAttribute('href', '/app/password')

    expect(within(menu).getByRole('radiogroup', { name: /theme/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /ada lovelace/i })).toHaveAttribute('aria-expanded', 'true')

    // Pinned because the E2E suite addresses this control by role: the
    // explicit role="menuitem" overrides the element's implicit button role,
    // so a query for a 'button' named Sign out finds nothing.
    expect(within(menu).getByRole('menuitem', { name: 'Sign out' })).toBeInTheDocument()
    expect(within(menu).queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument()
  })

  it('closes the menu when Escape is pressed', async () => {
    const user = userEvent.setup()
    renderMenu()

    await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
    expect(screen.getByRole('menu')).toBeInTheDocument()

    fireEvent.keyDown(document, { key: 'Escape' })

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('closes the menu when clicking outside it', async () => {
    const user = userEvent.setup()
    renderMenu()

    await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
    expect(screen.getByRole('menu')).toBeInTheDocument()

    fireEvent.mouseDown(document.body)

    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('calls signOut when "Sign out" is clicked', async () => {
    const user = userEvent.setup()
    const { signOut } = renderMenu()

    await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
    await user.click(screen.getByRole('menuitem', { name: /sign out/i }))

    expect(signOut).toHaveBeenCalledTimes(1)
  })
})
