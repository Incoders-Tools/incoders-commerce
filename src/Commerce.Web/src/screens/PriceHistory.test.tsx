import { render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceHistory } from './PriceHistory'
import type { PriceListEntryRecord } from '@/api/types'

const priceListId = '11111111-1111-1111-1111-111111111111'
const presentationId = '22222222-2222-2222-2222-222222222222'

const entries: PriceListEntryRecord[] = [
  {
    id: '33333333-3333-3333-3333-333333333333',
    organizationId: 'org-1',
    branchId: 'branch-1',
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
    branchId: 'branch-1',
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
 * history listing every `effective_from` descending", shown in the editor's side panel.
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

  it('fetches and displays the history when shown, newest first', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(entries), { status: 200 }))

    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    const items = await screen.findAllByRole('listitem')
    expect(items[0]).toHaveTextContent('2024-06-01')
    expect(items[0]).toHaveTextContent('550')
    expect(items[1]).toHaveTextContent('2024-01-01')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0][0]).toBe(
      `/pricing/price-lists/${priceListId}/presentations/${presentationId}/history`,
    )
  })

  it('shows an empty state with zero published entries', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }))

    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    await screen.findByText('Todavía no hay precios publicados.')
  })

  it('has no toggle of its own: the panel that shows it owns opening and closing', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(entries), { status: 200 }))

    render(<PriceHistory priceListId={priceListId} presentationId={presentationId} />)

    await screen.findAllByRole('listitem')
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
