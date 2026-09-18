import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CatalogScreen } from './CatalogScreen'
import { QuantityBehavior } from '@/api/types'
import type { PresentationRecord } from '@/api/types'

/**
 * design.md "Web: CatalogScreen rework": real presentation list +
 * identification-code editing, replacing the hand-typed rename form
 * (commerce-pricing-engine specs/catalog-item-identification/spec.md
 * "Requirement: Admin Editing of Identification Codes").
 */
describe('CatalogScreen', () => {
  const fetchMock = vi.fn()

  const unlabelled: PresentationRecord = {
    id: '11111111-1111-1111-1111-111111111111',
    organizationId: 'org-1',
    productId: '22222222-2222-2222-2222-222222222222',
    name: '1.5L bottle',
    quantityBehavior: QuantityBehavior.FixedQuantity,
    unitId: '33333333-3333-3333-3333-333333333333',
    identificationCode: null,
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
    updatedAtUtc: '2024-01-01T00:00:00Z',
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('lists presentations from GET /catalog/presentations', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByText('No code')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe('/catalog/presentations')
  })

  it("lets an admin set a Presentation's identification code via PUT", async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 })) // GET
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ ...unlabelled, identificationCode: '7791234567890' }), { status: 200 }),
      ) // PUT

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /edit code/i }))
    await user.type(screen.getByLabelText(/identification code/i), '7791234567890')
    await user.click(screen.getByRole('button', { name: /^save$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe(`/catalog/presentations/${unlabelled.id}`)
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toMatchObject({
      name: unlabelled.name,
      quantityBehavior: unlabelled.quantityBehavior,
      unitId: unlabelled.unitId,
      identificationCode: '7791234567890',
    })

    await screen.findByText('7791234567890')
    expect(screen.queryByText('No code')).not.toBeInTheDocument()
  })

  it('surfaces a visible error state when the API is unreachable, never stale/mock data', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<CatalogScreen />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/unreachable|error/i)
  })
})
