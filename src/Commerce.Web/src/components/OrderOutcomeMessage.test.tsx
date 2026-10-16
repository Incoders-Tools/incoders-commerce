import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { OrderOutcomeMessage } from './OrderOutcomeMessage'
import { OrderSubmissionOutcomeStatus, type OrderSubmissionOutcome } from '@/api/types'

const accepted = (orderNumber?: string): OrderSubmissionOutcome => ({
  status: OrderSubmissionOutcomeStatus.Accepted,
  reason: 'accepted',
  order: { orderId: 'a-guid', organizationId: 'org', status: 0, orderNumber, lines: [] },
})

describe('OrderOutcomeMessage', () => {
  it('shows the received order number with the explanation of its parts as a tooltip', () => {
    render(<OrderOutcomeMessage outcome={accepted('P01-W-37')} />)

    expect(screen.getByTestId('order-outcome')).toHaveTextContent('Pedido P01-W-37 recibido')
    const number = screen.getByText('P01-W-37')
    expect(number).toHaveClass('font-mono')
    expect(number).toHaveAttribute(
      'title',
      'P = Pedido · 01 = Sucursal · W = Web · 37 = número de pedido de la sucursal',
    )
  })

  it('never shows the order id', () => {
    render(<OrderOutcomeMessage outcome={accepted('P01-W-37')} />)

    expect(screen.getByTestId('order-outcome')).not.toHaveTextContent('a-guid')
  })

  it('shows the plain accepted message when the number is missing or unreadable', () => {
    const { rerender } = render(<OrderOutcomeMessage outcome={accepted()} />)
    expect(screen.getByTestId('order-outcome')).toHaveTextContent('Pedido aceptado.')

    rerender(<OrderOutcomeMessage outcome={accepted('not-a-number')} />)
    expect(screen.getByTestId('order-outcome')).toHaveTextContent('Pedido aceptado.')
    expect(screen.queryByTitle(/Sucursal/)).not.toBeInTheDocument()
  })

  it('shows the denial reason', () => {
    render(
      <OrderOutcomeMessage outcome={{ status: OrderSubmissionOutcomeStatus.Denied, reason: 'not-found', order: null }} />,
    )

    expect(screen.getByTestId('order-outcome')).toHaveTextContent('Rechazado: not-found')
  })
})
