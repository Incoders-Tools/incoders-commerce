import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { RowActions, type RowAction } from './RowActions'

function renderActions(actions: RowAction[]) {
  return render(
    <MemoryRouter initialEntries={['/list']}>
      <Routes>
        <Route path="/list" element={<RowActions actions={actions} label="Ana Pérez" />} />
        <Route path="/account" element={<p>account page</p>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('RowActions', () => {
  it('shows two actions as plain buttons, with no menu', () => {
    renderActions([
      { key: 'edit', label: 'Editar', onSelect: () => {} },
      { key: 'delete', label: 'Eliminar', onSelect: () => {} },
    ])

    expect(screen.getByRole('button', { name: 'Editar' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Eliminar' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /acciones de/i })).not.toBeInTheDocument()
  })

  it('keeps the first of three or more as a button and folds the rest into the menu', () => {
    const advance = vi.fn()
    renderActions([
      { key: 'edit', label: 'Editar', onSelect: () => {} },
      { key: 'advance', label: 'Adelanto', onSelect: advance },
      { key: 'deactivate', label: 'Dar de baja', onSelect: () => {}, destructive: true },
    ])

    expect(screen.getByRole('button', { name: 'Editar' })).toBeInTheDocument()
    expect(screen.queryByRole('menuitem', { name: 'Adelanto' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Acciones de Ana Pérez' }))
    expect(screen.getByRole('menu')).toBeInTheDocument()
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Adelanto', 'Dar de baja'])

    fireEvent.click(screen.getByRole('menuitem', { name: 'Adelanto' }))
    expect(advance).toHaveBeenCalledOnce()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('leaves hidden actions out, and closes the menu with Escape', () => {
    renderActions([
      { key: 'edit', label: 'Editar', onSelect: () => {} },
      { key: 'advance', label: 'Adelanto', onSelect: () => {}, hidden: true },
      { key: 'account', label: 'Cuenta', to: '/account' },
      { key: 'deactivate', label: 'Dar de baja', onSelect: () => {} },
    ])

    fireEvent.click(screen.getByRole('button', { name: 'Acciones de Ana Pérez' }))
    expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual(['Cuenta', 'Dar de baja'])

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  })

  it('navigates when a link action is chosen from the menu', () => {
    renderActions([
      { key: 'edit', label: 'Editar', onSelect: () => {} },
      { key: 'account', label: 'Cuenta', to: '/account' },
      { key: 'deactivate', label: 'Dar de baja', onSelect: () => {} },
    ])

    fireEvent.click(screen.getByRole('button', { name: 'Acciones de Ana Pérez' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Cuenta' }))

    expect(screen.getByText('account page')).toBeInTheDocument()
  })
})
