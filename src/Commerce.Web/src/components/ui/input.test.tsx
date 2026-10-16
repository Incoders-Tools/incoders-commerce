import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Input } from './input'

describe('Input', () => {
  it('uses semantic design tokens instead of raw Tailwind colors', () => {
    render(<Input aria-label="email" />)

    const input = screen.getByLabelText('email')
    expect(input).toHaveClass('border-input')
    expect(input).toHaveClass('bg-background')
    expect(input.className).not.toMatch(/border-neutral-300|bg-white|placeholder:text-neutral-400/)
  })
})
