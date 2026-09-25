import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Select } from './select'

describe('Select', () => {
  it('uses semantic design tokens instead of raw Tailwind colors', () => {
    render(
      <Select aria-label="kind">
        <option value="a">A</option>
      </Select>,
    )

    const select = screen.getByLabelText('kind')
    expect(select).toHaveClass('border-input')
    expect(select).toHaveClass('bg-background')
    expect(select.className).not.toMatch(/border-neutral-300|bg-white/)
  })
})
