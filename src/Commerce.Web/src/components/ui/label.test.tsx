import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Label } from './label'

describe('Label', () => {
  it('uses the semantic foreground token for text color', () => {
    render(<Label htmlFor="email">Email</Label>)

    const label = screen.getByText('Email')
    expect(label).toHaveClass('text-foreground')
  })
})
