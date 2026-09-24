import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Button } from './button'

describe('Button', () => {
  it('uses semantic design tokens instead of raw Tailwind colors for the default variant', () => {
    render(<Button>Save</Button>)

    const button = screen.getByRole('button', { name: 'Save' })
    expect(button).toHaveClass('bg-primary')
    expect(button).toHaveClass('text-primary-foreground')
    expect(button.className).not.toMatch(/bg-neutral-900/)
  })

  it('uses semantic destructive tokens', () => {
    render(<Button variant="destructive">Delete</Button>)

    const button = screen.getByRole('button', { name: 'Delete' })
    expect(button).toHaveClass('bg-destructive')
    expect(button).toHaveClass('text-destructive-foreground')
  })

  it('uses semantic border and background tokens for the outline variant', () => {
    render(<Button variant="outline">Cancel</Button>)

    const button = screen.getByRole('button', { name: 'Cancel' })
    expect(button).toHaveClass('border-border')
    expect(button).toHaveClass('bg-background')
    expect(button.className).not.toMatch(/border-neutral-300|bg-white/)
  })
})
