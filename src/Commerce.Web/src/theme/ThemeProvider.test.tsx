import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import type { SignedInResponse } from '@/api/types'
import { OrganizationBrandingContext } from './OrganizationBrandingProvider'
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

function renderAsUser(userId: string, primaryColor: string | null = null) {
  const user: SignedInResponse = {
    organizationId: 'org-1',
    userId,
    displayName: 'Test User',
    permissions: 0,
    isSystemAdmin: false,
  }
  return render(
    <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
      <OrganizationBrandingContext.Provider
        value={{ branding: { logoUrl: null, primaryColor }, loading: false }}
      >
        <ThemeProvider>
          <TestConsumer />
        </ThemeProvider>
      </OrganizationBrandingContext.Provider>
    </AuthContext.Provider>,
  )
}

const ORG_OVERRIDE_PROPERTIES = ['--primary', '--primary-foreground', '--ring']

describe('ThemeProvider', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.classList.remove('dark')
    for (const property of ORG_OVERRIDE_PROPERTIES) {
      document.documentElement.style.removeProperty(property)
    }
  })

  afterEach(() => {
    cleanup()
    document.documentElement.classList.remove('dark')
    for (const property of ORG_OVERRIDE_PROPERTIES) {
      document.documentElement.style.removeProperty(property)
    }
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

  it('applies the organization primary color as inline custom properties when custom is selected', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1', '#0a0a0a')

    await user.click(screen.getByText('custom'))

    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('#0a0a0a')
    expect(document.documentElement.style.getPropertyValue('--primary-foreground')).toBe('#fafafa')
    expect(document.documentElement.style.getPropertyValue('--ring')).toBe('#0a0a0a')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
  })

  it('clears the organization overrides when switching away from custom', async () => {
    const user = userEvent.setup()
    renderAsUser('user-1', '#0a0a0a')

    await user.click(screen.getByText('custom'))
    await user.click(screen.getByText('light'))

    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('')
  })

  it('falls back to plain light for a persisted custom preference when the org has no primary color', () => {
    window.localStorage.setItem('theme:user-1', 'custom')

    renderAsUser('user-1', null)

    expect(screen.getByTestId('theme')).toHaveTextContent('custom')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(document.documentElement.style.getPropertyValue('--primary')).toBe('')
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
