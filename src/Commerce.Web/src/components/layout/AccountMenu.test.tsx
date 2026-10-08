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
  selectableBranches: [],
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

    const changePasswordLink = within(menu).getByRole('menuitem', { name: /cambiar contraseña/i })
    expect(changePasswordLink).toHaveAttribute('href', '/app/password')

    expect(within(menu).getByRole('radiogroup', { name: /tema/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /ada lovelace/i })).toHaveAttribute('aria-expanded', 'true')

    // Pinned because the E2E suite addresses this control by role: the
    // explicit role="menuitem" overrides the element's implicit button role,
    // so a query for a 'button' named Sign out finds nothing.
    expect(within(menu).getByRole('menuitem', { name: 'Cerrar sesión' })).toBeInTheDocument()
    expect(within(menu).queryByRole('button', { name: 'Cerrar sesión' })).not.toBeInTheDocument()
  })

  it('shows an identifying icon on Change password and Sign out without changing their accessible names', async () => {
    const user = userEvent.setup()
    renderMenu()

    await user.click(screen.getByRole('button', { name: /ada lovelace/i }))
    const menu = screen.getByRole('menu')

    const changePasswordLink = within(menu).getByRole('menuitem', { name: 'Cambiar contraseña' })
    const changePasswordIcon = changePasswordLink.querySelector('svg')
    expect(changePasswordIcon).not.toBeNull()
    expect(changePasswordIcon).toHaveAttribute('aria-hidden', 'true')
    // A generic "has an svg" check would also pass for a hand-drawn one —
    // lucide-react stamps every icon with a `lucide` + `lucide-<name>`
    // class, so this is what actually proves it is the real KeyRound icon.
    expect(changePasswordIcon).toHaveClass('lucide', 'lucide-key-round')

    const signOutItem = within(menu).getByRole('menuitem', { name: 'Cerrar sesión' })
    const signOutIcon = signOutItem.querySelector('svg')
    expect(signOutIcon).not.toBeNull()
    expect(signOutIcon).toHaveAttribute('aria-hidden', 'true')
    expect(signOutIcon).toHaveClass('lucide', 'lucide-log-out')
  })

  it('replaces the hand-drawn chevron with a lucide ChevronDown icon on the trigger', () => {
    renderMenu()

    const trigger = screen.getByRole('button', { name: /ada lovelace/i })
    const icon = trigger.querySelector('svg')
    expect(icon).not.toBeNull()
    expect(icon).toHaveAttribute('aria-hidden', 'true')
    // Same discrimination as above: a hand-drawn chevron also has an
    // `aria-hidden` svg, so only the lucide class set proves which one it is.
    expect(icon).toHaveClass('lucide', 'lucide-chevron-down')
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
    await user.click(screen.getByRole('menuitem', { name: /cerrar sesión/i }))

    expect(signOut).toHaveBeenCalledTimes(1)
  })
})
