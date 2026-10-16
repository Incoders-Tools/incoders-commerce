import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Card } from './card'

describe('Card', () => {
  it('uses semantic design tokens instead of raw Tailwind colors', () => {
    render(<Card data-testid="card">content</Card>)

    const card = screen.getByTestId('card')
    expect(card).toHaveClass('bg-card')
    expect(card).toHaveClass('text-card-foreground')
    expect(card).toHaveClass('border-border')
    expect(card.className).not.toMatch(/border-neutral-200|bg-white/)
  })
})
