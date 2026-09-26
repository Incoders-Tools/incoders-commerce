import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceDateFilter } from './PriceDateFilter'
import type { PresentationRecord } from '@/api/types'

const presentation: PresentationRecord = {
  id: '22222222-2222-2222-2222-222222222222',
  organizationId: 'org-1',
  branchId: 'branch-1',
  productId: '33333333-3333-3333-3333-333333333333',
  name: '1.5L bottle',
  quantityBehavior: 0,
  unitId: '44444444-4444-4444-4444-444444444444',
  identificationCode: '7791234567890',
  createdAtUtc: '2024-01-01T00:00:00Z',
  createdByUserId: 'user-1',
  updatedAtUtc: '2024-01-01T00:00:00Z',
}

const entry = (unitPrice: number, effectiveFrom: string) => ({
  id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  organizationId: 'org-1',
  branchId: 'branch-1',
  priceListId: 'list-1',
  presentationId: presentation.id,
  unitPrice,
  effectiveFrom,
  source: 'Manual',
  importBatchId: null,
  createdAtUtc: `${effectiveFrom}T00:00:00Z`,
  createdByUserId: 'user-1',
})

describe('PriceDateFilter', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('loads "now" prices on mount with no query params', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([entry(1200, '2026-06-01')]), { status: 200 }))

    render(<PriceDateFilter priceListId="list-1" presentations={[presentation]} />)

    expect(await screen.findByText('1.5L bottle')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe('/pricing/price-lists/list-1/prices')
  })

  it('filters by a single as-of date', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 })) // mount load
    render(<PriceDateFilter priceListId="list-1" presentations={[presentation]} />)
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([entry(1000, '2026-03-01')]), { status: 200 }))

    const user = userEvent.setup()
    await user.type(screen.getByLabelText(/al día/i), '2026-05-15')
    await user.click(screen.getByRole('button', { name: /^aplicar$/i }))

    expect(await screen.findByText('$1000.00')).toBeInTheDocument()
    const url = fetchMock.mock.calls[1][0] as string
    expect(url).toBe('/pricing/price-lists/list-1/prices?asOf=2026-05-15')
  })

  it('filters by a from/to range', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }))
    render(<PriceDateFilter priceListId="list-1" presentations={[presentation]} />)
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))

    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify([entry(1000, '2026-03-01'), entry(1200, '2026-06-01')]), { status: 200 }),
    )

    const user = userEvent.setup()
    await user.type(screen.getByLabelText(/desde/i), '2026-05-01')
    await user.type(screen.getByLabelText(/hasta/i), '2026-06-30')
    await user.click(screen.getByRole('button', { name: /^aplicar$/i }))

    expect(await screen.findByText('$1000.00')).toBeInTheDocument()
    expect(screen.getByText('$1200.00')).toBeInTheDocument()
    const url = fetchMock.mock.calls[1][0] as string
    expect(url).toContain('from=2026-05-01')
    expect(url).toContain('to=2026-06-30')
  })

  it('clears the filter and returns to "now"', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([entry(1200, '2026-06-01')]), { status: 200 }))
    render(<PriceDateFilter priceListId="list-1" presentations={[presentation]} />)
    await screen.findByText('$1200.00')

    const user = userEvent.setup()
    await user.type(screen.getByLabelText(/al día/i), '2026-05-15')

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([entry(1200, '2026-06-01')]), { status: 200 }))
    await user.click(screen.getByRole('button', { name: /^limpiar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/pricing/price-lists/list-1/prices')
    expect(screen.getByLabelText(/al día/i)).toHaveValue('')
  })
})
