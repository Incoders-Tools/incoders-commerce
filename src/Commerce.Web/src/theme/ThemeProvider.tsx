import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { useOptionalAuth } from '@/auth/AuthContext'
import { getOrganizationThemeOverrides } from './organizationTheme'

export type Theme = 'light' | 'dark' | 'custom'

interface ThemeContextValue {
  theme: Theme
  setTheme: (theme: Theme) => void
}

const ThemeContext = createContext<ThemeContextValue | undefined>(undefined)

const DEFAULT_THEME: Theme = 'light'

function isTheme(value: string | null): value is Theme {
  return value === 'light' || value === 'dark' || value === 'custom'
}

function storageKeyFor(userId: string | undefined): string {
  return `theme:${userId ?? 'anonymous'}`
}

function readStoredTheme(storageKey: string): Theme {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return isTheme(stored) ? stored : DEFAULT_THEME
  } catch {
    // localStorage unavailable (private browsing, disabled storage, etc.) —
    // fall back to the default theme instead of throwing.
    return DEFAULT_THEME
  }
}

/**
 * 3-way theme system (design.md T2): light, dark, and org-scoped custom.
 * Persists per authenticated user (localStorage keyed by user id, via
 * `useOptionalAuth` so this also works unauthenticated on public routes)
 * and toggles the `.dark` class on `<html>` consumed by T1's semantic
 * design tokens.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const auth = useOptionalAuth()
  const userId = auth?.user?.userId
  const storageKey = storageKeyFor(userId)

  const [theme, setThemeState] = useState<Theme>(() => readStoredTheme(storageKey))

  // Re-read the persisted preference whenever the effective identity changes
  // (sign-in/sign-out on the same device) so each account restores its own
  // choice instead of inheriting the previous session's.
  useEffect(() => {
    setThemeState(readStoredTheme(storageKey))
    // storageKey already encodes userId; re-running on its change is exactly
    // the intended behavior.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey])

  useEffect(() => {
    const root = document.documentElement
    // "custom" is a placeholder consumer of org-level tokens until T6 wires
    // a real organization theme in (see organizationTheme.ts). While
    // getOrganizationThemeOverrides() returns null, "custom" must render as
    // a safe fallback to "light" rather than leaving stale/no styling.
    const overrides = theme === 'custom' ? getOrganizationThemeOverrides() : null

    if (theme === 'dark') {
      root.classList.add('dark')
    } else {
      root.classList.remove('dark')
    }

    // Future T6: when overrides is non-null, inject them as inline CSS
    // custom properties on `root` (or a generated <style> tag) here instead
    // of toggling `.dark` — a custom palette is independent of light/dark.
    void overrides
  }, [theme])

  const setTheme = (next: Theme) => {
    setThemeState(next)
    try {
      window.localStorage.setItem(storageKey, next)
    } catch {
      // Persistence is best-effort; an unavailable localStorage must not
      // block the theme from applying for the current session.
    }
  }

  return <ThemeContext.Provider value={{ theme, setTheme }}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext)
  if (!context) {
    throw new Error('useTheme must be used within a ThemeProvider')
  }
  return context
}
