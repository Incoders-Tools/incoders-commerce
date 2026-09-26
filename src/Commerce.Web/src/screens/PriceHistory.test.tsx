import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceHistory } from './PriceHistory'
import type { PriceListEntryRecord } from '@/api/types'

const priceListId = '11111111-1111-1111-1111-111111111111'
const presentationId = '22222222-2222-2222-2222-222222222222'

const entries: PriceListEntryRecord[] = [
  {
    id: '33333333-3333-3333-3333-333333333333',
    organizationId: 'org-1',
    priceListId,
    presentationId,
    unitPrice: 550,
    effectiveFrom: '2024-06-01',
    source: 'Manual',
    importBatchId: null,
    createdAtUtc: '2024-06-01T00:00:00Z',
    createdByUserId: 'user-1',
  },
  {
    id: '44444444-4444-4444-4444-444444444444',
    organizationId: 'org-1',
    priceListId,
    presentationId,
    unitPrice: 500,
    effectiveFrom: '2024-01-01',
    source: 'Manual',
    importBatchId: null,
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
  },
]

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`": "a
 * per-row History expander listing every `effective_from` descending".
 */
describe('PriceHistory', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('is collapsed by default and fetches nothing', () => {
    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    expect(screen.queryByText(/2024-06-01/)).not.toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('expands to fetch and display history, newest first', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(entries), { status: 200 }))

    const user = userEvent.setup()
    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    await user.click(screen.getByRole('button', { name: /historial/i }))

    const items = await screen.findAllByRole('listitem')
    expect(items[0]).toHaveTextContent('2024-06-01')
    expect(items[0]).toHaveTextContent('550')
    expect(items[1]).toHaveTextContent('2024-01-01')
    expect(fetchMock.mock.calls[0][0]).toBe(
      `/pricing/price-lists/${priceListId}/presentations/${presentationId}/history`,
    )
  })

  it('shows an empty state with zero published entries', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }))

    const user = userEvent.setup()
    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    await user.click(screen.getByRole('button', { name: /historial/i }))

    await screen.findByText('Todavía no hay precios publicados.')
  })

  it('collapses again without a second fetch', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(entries), { status: 200 }))

    const user = userEvent.setup()
    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    await user.click(screen.getByRole('button', { name: /historial/i }))
    await screen.findAllByRole('listitem')
    await user.click(screen.getByRole('button', { name: /ocultar historial/i }))

    expect(screen.queryByText(/2024-06-01/)).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /historial/i }))
    expect(await screen.findAllByRole('listitem')).toHaveLength(2)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
