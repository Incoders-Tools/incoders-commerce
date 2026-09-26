import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { OrganizationBrandingContext } from '@/theme/OrganizationBrandingProvider'
import { ThemeProvider } from '@/theme/ThemeProvider'
import { ThemeSwitcher } from './ThemeSwitcher'

describe('ThemeSwitcher', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.classList.remove('dark')
  })

  afterEach(() => {
    cleanup()
    document.documentElement.classList.remove('dark')
  })

  it('renders exactly 3 options: Claro, Oscuro, Personalizado', () => {
    render(
      <ThemeProvider>
        <ThemeSwitcher />
      </ThemeProvider>,
    )

    expect(screen.getByRole('radio', { name: /claro/i })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /oscuro/i })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /personalizado/i })).toBeInTheDocument()
  })

  it('marks light as selected by default and switches selection + applies the dark class on click', async () => {
    const user = userEvent.setup()
    render(
      <ThemeProvider>
        <ThemeSwitcher />
      </ThemeProvider>,
    )

    expect(screen.getByRole('radio', { name: /claro/i })).toHaveAttribute('aria-checked', 'true')

    await user.click(screen.getByRole('radio', { name: /oscuro/i }))

    expect(screen.getByRole('radio', { name: /oscuro/i })).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('radio', { name: /claro/i })).toHaveAttribute('aria-checked', 'false')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
  })

  it('disables Custom when the organization has no primary color', () => {
    render(
      <OrganizationBrandingContext.Provider value={{ branding: { logoUrl: null, primaryColor: null }, loading: false }}>
        <ThemeProvider>
          <ThemeSwitcher />
        </ThemeProvider>
      </OrganizationBrandingContext.Provider>,
    )

    expect(screen.getByRole('radio', { name: 'Personalizado' })).toBeDisabled()
  })

  it('enables Custom when the organization has a primary color', () => {
    render(
      <OrganizationBrandingContext.Provider
        value={{ branding: { logoUrl: null, primaryColor: '#336699' }, loading: false }}
      >
        <ThemeProvider>
          <ThemeSwitcher />
        </ThemeProvider>
      </OrganizationBrandingContext.Provider>,
    )

    expect(screen.getByRole('radio', { name: 'Personalizado' })).not.toBeDisabled()
  })
})
