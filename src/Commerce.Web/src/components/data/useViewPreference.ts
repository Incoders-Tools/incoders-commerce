import { useState } from 'react'
import type { DataViewMode } from './ViewSwitch'

const DEFAULT_VIEW: DataViewMode = 'table'

function isViewMode(value: string | null): value is DataViewMode {
  return value === 'table' || value === 'cards'
}

function storageKeyFor(screenKey: string): string {
  return `view:${screenKey}`
}

function readStoredView(storageKey: string): DataViewMode {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return isViewMode(stored) ? stored : DEFAULT_VIEW
  } catch {
    // localStorage unavailable (private browsing, disabled storage, …) —
    // same tolerant fallback ThemeProvider uses.
    return DEFAULT_VIEW
  }
}

/**
 * T4: per-screen table/cards preference, persisted under `view:<screenKey>`
 * (e.g. `view:catalog`). Read/write are both best-effort and corrupt values
 * fall back to the default, following `theme/ThemeProvider.tsx`.
 */
export function useViewPreference(screenKey: string): [DataViewMode, (view: DataViewMode) => void] {
  const storageKey = storageKeyFor(screenKey)
  const [view, setViewState] = useState<DataViewMode>(() => readStoredView(storageKey))

  const setView = (next: DataViewMode) => {
    setViewState(next)
    try {
      window.localStorage.setItem(storageKey, next)
    } catch {
      // Persistence is best-effort; it must not block the switch from
      // applying for the current session.
    }
  }

  return [view, setView]
}
