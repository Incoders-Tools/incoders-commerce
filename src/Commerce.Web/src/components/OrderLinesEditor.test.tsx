import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { OrderLinesEditor } from './OrderLinesEditor'
import type { PresentationRecord, SubmitOrderLine } from '@/api/types'

/**
 * commerce-guest-ordering design.md "One screen, guest and registered as
 * peers" / tasks.md 6.3-6.4: a shared presentation-picker line editor fed by
 * the public catalog read, reused by both the guest and registered
 * branches — no raw GUID input field anywhere on the guest path.
 */
describe('OrderLinesEditor', () => {
  const presentations: PresentationRecord[] = [
    {
      id: 'pres-1',
      organizationId: 'org-1',
      branchId: 'branch-1',
      productId: 'prod-1',
      name: '1.5L bottle',
      quantityBehavior: 0,
      unitId: 'unit-1',
      identificationCode: '111',
      createdAtUtc: '2024-01-01T00:00:00Z',
      createdByUserId: 'user-1',
      updatedAtUtc: '2024-01-01T00:00:00Z',
    },
    {
      id: 'pres-2',
      organizationId: 'org-1',
      branchId: 'branch-1',
      productId: 'prod-2',
      name: '2kg bag',
      quantityBehavior: 0,
      unitId: 'unit-1',
      identificationCode: null,
      createdAtUtc: '2024-01-01T00:00:00Z',
      createdByUserId: 'user-1',
      updatedAtUtc: '2024-01-01T00:00:00Z',
    },
  ]

  it('renders a presentation picker by name and no raw GUID text input', () => {
    render(<OrderLinesEditor presentations={presentations} lines={[]} onChange={vi.fn()} />)

    expect(screen.getByRole('combobox', { name: /presentación/i })).toBeInTheDocument()
    expect(screen.getByText('1.5L bottle')).toBeInTheDocument()
    expect(screen.getByText('2kg bag')).toBeInTheDocument()

    // No text input labeled "Presentation ID"/"Product ID" — the raw-GUID
    // shape this component replaces.
    expect(screen.queryByLabelText(/presentation id/i)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/product id/i)).not.toBeInTheDocument()
  })

  it('adds a line for the selected presentation and quantity via onChange', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<OrderLinesEditor presentations={presentations} lines={[]} onChange={onChange} />)

    await user.selectOptions(screen.getByRole('combobox', { name: /presentación/i }), 'pres-2')
    await user.clear(screen.getByLabelText(/cantidad/i))
    await user.type(screen.getByLabelText(/cantidad/i), '3')
    await user.click(screen.getByRole('button', { name: /agregar línea/i }))

    expect(onChange).toHaveBeenCalledWith([{ productId: 'prod-2', presentationId: 'pres-2', quantity: 3 }])
  })

  it('uses semantic design tokens for the presentation select, dark-mode-safe', () => {
    render(<OrderLinesEditor presentations={presentations} lines={[]} onChange={vi.fn()} />)

    const select = screen.getByRole('combobox', { name: /presentación/i })
    expect(select.className).not.toMatch(/border-neutral-300|bg-white/)
  })

  it('lists already-added lines by presentation name and supports removal', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    const lines: SubmitOrderLine[] = [{ productId: 'prod-1', presentationId: 'pres-1', quantity: 2 }]
    render(<OrderLinesEditor presentations={presentations} lines={lines} onChange={onChange} />)

    expect(screen.getByText(/1\.5L bottle.*cant\.:\s*2/i)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /quitar/i }))
    expect(onChange).toHaveBeenCalledWith([])
  })
})
