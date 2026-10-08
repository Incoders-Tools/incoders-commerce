import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { StockHistoryPage, StockMovement } from '@/api/types'
import { StockMovementsScreen } from './StockMovementsScreen'
import { json } from './supplierFixtures'

const movement = (overrides: Partial<StockMovement>): StockMovement => ({
  id: 'm-1',
  kind: 'PurchaseReceipt',
  quantity: 120,
  occurredAtUtc: '2026-10-01T12:00:00Z',
  reason: null,
  lotCode: 'L-77',
  sourceType: 'PurchaseReception',
  sourceId: 'rec-1',
  sourceNumber: 'R01-W-1',
  reversesMovementId: null,
  createdByUserId: 'u-1',
  balanceAfter: 120,
  ...overrides,
})

describe('StockMovementsScreen', () => {
  const fetchMock = vi.fn()
  let page: StockHistoryPage

  beforeEach(() => {
    page = {
      presentationId: 'pr-1',
      onHand: 117.5,
      total: 2,
      page: 1,
      pageSize: 25,
      items: [
        movement({ id: 'm-2', kind: 'Shrinkage', quantity: -2.5, sourceType: null, sourceId: null, sourceNumber: null, reason: 'Se venció', lotCode: null, balanceAfter: 117.5 }),
        movement({}),
      ],
    }
    fetchMock.mockImplementation(async () => json(page))
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const renderScreen = () =>
    render(
      <MemoryRouter
        initialEntries={[
          { pathname: '/app/stock/pr-1/movements', state: { label: 'Media res — Kilo', behavior: 'Weighted' } },
        ]}
      >
        <Routes>
          <Route path="/app/stock" element={<p>Lista de stock</p>} />
          <Route path="/app/stock/:presentationId/movements" element={<StockMovementsScreen />} />
        </Routes>
      </MemoryRouter>,
    )

  it('shows each movement in Spanish with its signed quantity, balance after and source link', async () => {
    renderScreen()

    expect(await screen.findByText('Media res — Kilo')).toBeInTheDocument()
    expect(screen.getByText('En stock: 117,5 kg')).toBeInTheDocument()
    const rows = within(await screen.findByRole('table')).getAllByRole('row')

    const shrinkage = rows[1]
    expect(within(shrinkage).getByText('Merma')).toBeInTheDocument()
    expect(within(shrinkage).getByText('-2,5 kg')).toBeInTheDocument()
    expect(within(shrinkage).getByText('117,5 kg')).toBeInTheDocument()
    expect(within(shrinkage).getByText('Se venció')).toBeInTheDocument()

    const receipt = rows[2]
    expect(within(receipt).getByText('Recepción de compra')).toBeInTheDocument()
    expect(within(receipt).getByText('+120 kg')).toBeInTheDocument()
    expect(within(receipt).getByRole('link', { name: 'R01-W-1' })).toHaveAttribute('href', '/app/receptions/rec-1')
  })

  it('pages through the history', async () => {
    page = { ...page, total: 60 }
    const user = userEvent.setup()
    renderScreen()

    expect(await screen.findByText('Página 1 de 3')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Siguiente' }))

    await waitFor(() =>
      expect(fetchMock.mock.calls.map((call) => call[0])).toContain('/stock/pr-1/movements?page=2&pageSize=25'),
    )
  })

  it('shows an empty state and a load error', async () => {
    page = { ...page, total: 0, items: [] }
    const { unmount } = renderScreen()
    expect(await screen.findByText('Esta presentación todavía no tiene movimientos.')).toBeInTheDocument()
    unmount()

    fetchMock.mockImplementation(async () => json({ title: 'boom' }, 500))
    renderScreen()
    expect(await screen.findByRole('alert')).toHaveTextContent('boom')
  })
})
