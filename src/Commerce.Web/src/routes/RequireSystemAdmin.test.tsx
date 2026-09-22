import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/auth/AuthContext'
import { RequireSystemAdmin } from './RequireSystemAdmin'

function renderTree(user: unknown) {
  return render(<AuthContext.Provider value={{ user, error: null, signIn: vi.fn(), signOut: vi.fn() } as never}><MemoryRouter initialEntries={['/app/organizations']}><Routes><Route path="/app/catalog" element={<div>Catalog content</div>} /><Route element={<RequireSystemAdmin />}><Route path="/app/organizations" element={<div>Organizations content</div>} /></Route></Routes></MemoryRouter></AuthContext.Provider>)
}

describe('RequireSystemAdmin', () => {
  it('renders the guarded Outlet for a system administrator', () => { renderTree({ organizationId: 'org', userId: 'user', displayName: 'Sys', permissions: 0, isSystemAdmin: true }); expect(screen.getByText('Organizations content')).toBeInTheDocument() })
  it('redirects a business administrator and renders no guarded content', () => { renderTree({ organizationId: 'org', userId: 'user', displayName: 'Admin', permissions: 4, isSystemAdmin: false }); expect(screen.getByText('Catalog content')).toBeInTheDocument(); expect(screen.queryByText('Organizations content')).not.toBeInTheDocument() })
  it('default-denies a null user', () => { renderTree(null); expect(screen.getByText('Catalog content')).toBeInTheDocument() })
})
