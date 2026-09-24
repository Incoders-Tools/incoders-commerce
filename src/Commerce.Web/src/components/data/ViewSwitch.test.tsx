import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ViewSwitch } from './ViewSwitch'

/**
 * T4: reusable table/cards view switch. Same visual + a11y pattern as
 * `components/theme/ThemeSwitcher.tsx` (segmented `role="radiogroup"` with
 * inline SVG icons, no icon library).
 */
describe('ViewSwitch', () => {
  it('exposes both views as radios inside a labelled radiogroup', () => {
    render(<ViewSwitch value="table" onChange={() => {}} />)

    expect(screen.getByRole('radiogroup', { name: /view/i })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /table view/i })).toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /card view/i })).toBeInTheDocument()
  })

  it('reflects the selected view as the checked radio', () => {
    const { rerender } = render(<ViewSwitch value="table" onChange={() => {}} />)

    expect(screen.getByRole('radio', { name: /table view/i })).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveAttribute('aria-checked', 'false')

    rerender(<ViewSwitch value="cards" onChange={() => {}} />)

    expect(screen.getByRole('radio', { name: /table view/i })).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('reports the picked view to onChange', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<ViewSwitch value="table" onChange={onChange} />)

    await user.click(screen.getByRole('radio', { name: /card view/i }))

    expect(onChange).toHaveBeenCalledWith('cards')
  })

  it('is reachable and operable from the keyboard', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<ViewSwitch value="table" onChange={onChange} />)

    await user.tab()
    expect(screen.getByRole('radio', { name: /table view/i })).toHaveFocus()

    await user.tab()
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveFocus()

    await user.keyboard('{Enter}')
    expect(onChange).toHaveBeenCalledWith('cards')
  })
})
