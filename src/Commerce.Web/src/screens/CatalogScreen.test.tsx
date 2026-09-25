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

  const labelled: PresentationRecord = {
    ...unlabelled,
    id: '44444444-4444-4444-4444-444444444444',
    name: '330ml can',
    identificationCode: '7790000000001',
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
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

  // T4: the shared data-view layer (PageHeader + DataToolbar + DataView).
  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    const { container } = render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(container.querySelector('.max-w-2xl')).toBeNull()
    expect(container.querySelector('.mx-auto')).toBeNull()
  })

  it('renders an empty state message when the catalog has no presentations', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 }))

    render(<CatalogScreen />)

    expect(await screen.findByText(/no presentations/i)).toBeInTheDocument()
  })

  it('filters the listed presentations client-side by name or identification code', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByText('330ml can')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/search presentations/i), '330')

    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
    // Filtering is purely client-side over what was already loaded.
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.clear(screen.getByLabelText(/search presentations/i))
    await user.type(screen.getByLabelText(/search presentations/i), '7790000000001')

    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.type(screen.getByLabelText(/search presentations/i), 'zzzz')

    expect(screen.getByText(/no presentations match/i)).toBeInTheDocument()
  })

  it('switches to the card view and restores that preference on remount', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    const user = userEvent.setup()
    const first = render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /card view/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)
    expect(window.localStorage.getItem('view:catalog')).toBe('cards')

    first.unmount()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /card view/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still edits the identification code from the card view', async () => {
    window.localStorage.setItem('view:catalog', 'cards')
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

    await screen.findByText('7791234567890')
  })

  it('replaces the list with a full-screen edit page instead of expanding the row inline', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getAllByRole('button', { name: /edit code/i })[0])

    // The list (and the other presentation's row) is gone, not just a form
    // appended under this row.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByText('330ml can')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Edit code' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /back to catalog/i }))

    expect(screen.getByText('1.5L bottle')).toBeInTheDocument()
    expect(screen.getByText('330ml can')).toBeInTheDocument()
  })

  it('keeps the search text and view preference after returning from the edit page', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('radio', { name: /card view/i }))
    await user.type(screen.getByLabelText(/search presentations/i), '330')
    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /edit code/i }))
    await user.click(screen.getByRole('button', { name: /back to catalog/i }))

    expect(screen.getByLabelText(/search presentations/i)).toHaveValue('330')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
  })
})
