import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ResetPasswordRoute } from './ResetPasswordRoute'

/**
 * web-app-routing spec: "Reset-Password Uses a Path Param, Not a Query
 * String" / "Legacy query-string link is not supported post-deploy".
 */
function renderAt(initialEntry: string) {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/reset-password/:token" element={<ResetPasswordRoute />} />
        <Route path="/reset-password" element={<ResetPasswordRoute />} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('ResetPasswordRoute', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('passes the path-param token to ResetPasswordScreen', () => {
    renderAt('/reset-password/abc')

    expect(screen.getByRole('heading', { name: /restablecer contraseña/i })).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('renders the invalid-link treatment for a bare /reset-password with no token, and makes no request', () => {
    renderAt('/reset-password')

    expect(screen.getByText('Este enlace de restablecimiento no es válido o venció.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
