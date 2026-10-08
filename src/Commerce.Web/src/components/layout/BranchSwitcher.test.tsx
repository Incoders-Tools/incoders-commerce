import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import { BranchSwitcher } from './BranchSwitcher'

// admin-console spec, "Top Navbar Branch Switcher": hidden with no
// selectable branch, a static label for exactly one, a real switcher for
// several.

function renderWithBranches(
  selectableBranches: { id: string; name: string; code: number }[],
  selectedBranch: { id: string; name: string; code: number } | null,
  selectBranch = vi.fn(),
) {
  return render(
    <BranchContext.Provider value={{ selectedBranch, selectableBranches, selectBranch }}>
      <BranchSwitcher />
    </BranchContext.Provider>,
  )
}

describe('BranchSwitcher', () => {
  it('renders nothing when there is no selectable branch', () => {
    const { container } = renderWithBranches([], null)
    expect(container).toBeEmptyDOMElement()
  })

  it('shows a static label instead of a dropdown for a single branch', () => {
    renderWithBranches([{ id: 'b1', name: 'Ruta 51', code: 1 }], { id: 'b1', name: 'Ruta 51', code: 1 })

    expect(screen.getByText('Ruta 51')).toBeInTheDocument()
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('lists every selectable branch and shows the current one selected', () => {
    renderWithBranches(
      [
        { id: 'b1', name: 'Ruta 51', code: 1 },
        { id: 'b2', name: 'Centro', code: 2 },
      ],
      { id: 'b2', name: 'Centro', code: 2 },
    )

    const select = screen.getByRole('combobox', { name: 'Sucursal' })
    expect(select).toHaveValue('b2')
    expect(screen.getByRole('option', { name: '01 · Ruta 51' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: '02 · Centro' })).toBeInTheDocument()
  })

  it('calls selectBranch with the chosen branch on switch', async () => {
    const user = userEvent.setup()
    const selectBranch = vi.fn()
    renderWithBranches(
      [
        { id: 'b1', name: 'Ruta 51', code: 1 },
        { id: 'b2', name: 'Centro', code: 2 },
      ],
      { id: 'b1', name: 'Ruta 51', code: 1 },
      selectBranch,
    )

    await user.selectOptions(screen.getByRole('combobox', { name: 'Sucursal' }), 'b2')

    expect(selectBranch).toHaveBeenCalledWith({ id: 'b2', name: 'Centro', code: 2 })
  })
})
