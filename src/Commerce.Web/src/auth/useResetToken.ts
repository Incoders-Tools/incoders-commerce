import { useState } from 'react'

/**
 * Reads the `?token=` query parameter once at mount (commerce-password-
 * recovery design.md "Reset link, no router") — no router dependency is
 * added; `Program.cs`'s existing `MapFallbackToFile("index.html")` already
 * serves `/reset-password?token=...`. `clear()` scrubs the token from the
 * URL/history via `history.replaceState` so a page refresh after a
 * successful reset does not re-show the reset screen.
 */
export function useResetToken(): { token: string | null; clear: () => void } {
  const [token, setToken] = useState<string | null>(
    () => new URLSearchParams(window.location.search).get('token'),
  )

  const clear = () => {
    window.history.replaceState({}, '', '/')
    setToken(null)
  }

  return { token, clear }
}
