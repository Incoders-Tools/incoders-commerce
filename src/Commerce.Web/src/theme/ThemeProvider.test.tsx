import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import type { SignedInResponse } from '@/api/types'
import { ThemeProvider, useTheme } from './ThemeProvider'

function TestConsumer() {
  const { theme, setTheme } = useTheme()
  return (
    <div>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme('light')}>light</button>
      <button onClick={() => setTheme('dark')}>dark</button>
      <button onClick={() => setTheme('custom')}>custom</button>
    </div>
  )
}

function renderAsUser(userId: string) {
  const user: SignedInResponse = {
    organizationId: 'org-1',
    userId,
    displayName: 'Test User',
    permissions: 0,
    isSystemAdmin: false,
  }
  return render(
    <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
      <ThemeProvider>
        <TestConsumer />
      </ThemeProvider>
    </AuthContext.Provider>,
  )
}

describe('ThemeProvider', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.classList.remove('dark')
  })

  afterEach(() => {
    cleanup()
    document.documentElement.classList.remove('dark')
  })

  it('applies the dark class on document.documentElement when dark is selected', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1')

    await user.click(screen.getByText('dark'))

    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('removes the dark class when switching back to light', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1')

    await user.click(screen.getByText('dark'))
    await user.click(screen.getByText('light'))

    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('does not apply the dark class for custom theme when there are no org overrides', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1')

    await user.click(screen.getByText('custom'))

    expect(screen.getByTestId('theme')).toHaveTextContent('custom')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('persists the theme choice in localStorage keyed by user id', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1')

    await user.click(screen.getByText('dark'))

    expect(window.localStorage.getItem('theme:user-1')).toBe('dark')
  })

  it('restores the persisted theme for the same user on remount', () => {
    window.localStorage.setItem('theme:user-1', 'dark')

    renderAsUser('user-1')

    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(screen.getByTestId('theme')).toHaveTextContent('dark')
  })

  it('keeps separate persisted themes per user', () => {
    window.localStorage.setItem('theme:user-1', 'dark')
    window.localStorage.setItem('theme:user-2', 'light')

    renderAsUser('user-2')

    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(screen.getByTestId('theme')).toHaveTextContent('light')
  })

  it('falls back to an anonymous storage key when no user is authenticated', async () => {
    const user = userEvent.setup()
    render(
      <ThemeProvider>
        <TestConsumer />
      </ThemeProvider>,
    )

    await user.click(screen.getByText('dark'))

    expect(window.localStorage.getItem('theme:anonymous')).toBe('dark')
  })
})
