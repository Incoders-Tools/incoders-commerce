import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceListsScreen } from './PriceListsScreen'
import { RequireAdmin } from '@/routes/RequireAdmin'
import { AuthContext } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import type { PresentationRecord, PriceListRecord } from '@/api/types'

const defaultPriceList: PriceListRecord = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  name: 'Default',
  isDefault: true,
  createdAtUtc: '2024-01-01T00:00:00Z',
  createdByUserId: 'user-1',
}

const seasonalPriceList: PriceListRecord = {
  id: '99999999-9999-9999-9999-999999999999',
  organizationId: 'org-1',
  name: 'Seasonal',
  isDefault: false,
  createdAtUtc: '2024-03-05T00:00:00Z',
  createdByUserId: 'user-1',
}

const presentation: PresentationRecord = {
  id: '22222222-2222-2222-2222-222222222222',
  organizationId: 'org-1',
  productId: '33333333-3333-3333-3333-333333333333',
  name: '1.5L bottle',
  quantityBehavior: 0,
  unitId: '44444444-4444-4444-4444-444444444444',
  identificationCode: '7791234567890',
  createdAtUtc: '2024-01-01T00:00:00Z',
  createdByUserId: 'user-1',
  updatedAtUtc: '2024-01-01T00:00:00Z',
}

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`":
 * nested inside the existing `RequireAdmin` block, no new guard component.
 */
function renderGuardedPriceLists(authValue: { user: unknown }) {
  return render(
    <AuthContext.Provider value={authValue as never}>
      <MemoryRouter initialEntries={['/app/price-lists']}>
        <Routes>
          <Route path="/app/catalog" element={<div>Catalog content</div>} />
          <Route element={<RequireAdmin />}>
            <Route path="/app/price-lists" element={<PriceListsScreen />} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  )
}

describe('PriceListsScreen routing', () => {
  it('redirects a seller (no ManageUsers bit) to /app/catalog', () => {
    renderGuardedPriceLists({
      user: { organizationId: 'org-1', userId: 'user-1', displayName: 'Sam', permissions: Permission.ViewSales },
    })

    expect(screen.getByText('Catalog content')).toBeInTheDocument()
    expect(screen.queryByText('Prices')).not.toBeInTheDocument()
  })
})

describe('PriceListsScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    // T4c: the table/cards preference now persists between cases.
    window.localStorage.clear()
  })

  /** The two calls every mount makes, in order. */
  const loadOnce = (lists: PriceListRecord[], items: PresentationRecord[] = [presentation]) =>
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify(lists), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(items), { status: 200 }))

  it('loads the price lists under Prices', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([defaultPriceList]), { status: 200 })) // GET /pricing/price-lists
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 })) // GET /catalog/presentations

    render(<PriceListsScreen />)

    await screen.findByText('Default')
    expect(fetchMock.mock.calls[0][0]).toBe('/pricing/price-lists')
    expect(fetchMock.mock.calls[1][0]).toBe('/catalog/presentations')
  })

  it('offers to create a default price list when none exists yet', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([]), { status: 200 })) // no price lists
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(defaultPriceList), { status: 201 })) // create default

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByRole('button', { name: /create default price list/i })
    await user.click(screen.getByRole('button', { name: /create default price list/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[2]
    expect(url).toBe('/pricing/price-lists')
    expect(JSON.parse(init.body as string)).toMatchObject({ isDefault: true })
  })

  it('publishes a new price through the default price list', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([defaultPriceList]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 }))
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({
            id: '55555555-5555-5555-5555-555555555555',
            organizationId: 'org-1',
            priceListId: defaultPriceList.id,
            presentationId: presentation.id,
            unitPrice: 600,
            effectiveFrom: '2024-07-01',
            source: 'Manual',
            importBatchId: null,
            createdAtUtc: '2024-07-01T00:00:00Z',
            createdByUserId: 'user-1',
          }),
          { status: 201 },
        ),
      )

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByText('Default')
    await user.click(screen.getByRole('button', { name: /manage prices/i }))
    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /new price/i }))
    await user.type(screen.getByLabelText('Unit price'), '600')
    await user.type(screen.getByLabelText('Effective from'), '2024-07-01')
    await user.click(screen.getByRole('button', { name: /^publish$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[2]
    expect(url).toBe(`/pricing/price-lists/${defaultPriceList.id}/entries`)
    expect(JSON.parse(init.body as string)).toMatchObject({
      presentationId: presentation.id,
      unitPrice: 600,
      effectiveFrom: '2024-07-01',
    })
  })

  it('shows the Suppliers tab as a structural placeholder', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([defaultPriceList]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 }))

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByText('Default')
    await user.click(screen.getByRole('button', { name: /^suppliers$/i }))
    expect(screen.getByText(/supplier mappings/i)).toBeInTheDocument()
  })

  const mapping = {
    id: '66666666-6666-6666-6666-666666666666',
    organizationId: 'org-1',
    supplierName: 'Acme',
    sheetName: 'Prices',
    headerRow: 1,
    codeColumn: 'A',
    priceColumn: 'B',
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
  }

  const batch = {
    id: '77777777-7777-7777-7777-777777777777',
    organizationId: 'org-1',
    supplierMappingId: mapping.id,
    fileName: 'prices.xlsx',
    rowCount: 1,
    status: 'Staged',
    uploadedAtUtc: '2024-07-01T00:00:00Z',
    uploadedByUserId: 'user-1',
    resolvedAtUtc: null,
  }

  const batchDetail = {
    batch,
    rows: [
      {
        id: '88888888-8888-8888-8888-888888888888',
        organizationId: 'org-1',
        batchId: batch.id,
        rowNumber: 2,
        rawCode: presentation.identificationCode,
        rawPrice: '600',
        presentationId: presentation.id,
        currentPrice: null,
        proposedPrice: 600,
        matchStatus: 'Matched',
        rejectReason: null,
      },
    ],
  }

  it('uploads a file, renders the real review table, and commits through the endpoint', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([defaultPriceList]), { status: 200 })) // GET /pricing/price-lists
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 })) // GET /catalog/presentations

    const user = userEvent.setup()
    render(<PriceListsScreen />)
    await screen.findByText('Default')

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([mapping]), { status: 200 })) // GET /pricing/supplier-mappings
    await user.click(screen.getByRole('button', { name: /^import$/i }))
    await screen.findByLabelText(/supplier/i)

    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify(batch), { status: 201 })) // POST /pricing/imports
      .mockResolvedValueOnce(new Response(JSON.stringify(batchDetail), { status: 200 })) // GET /pricing/imports/{id}

    const file = new File(['dummy'], 'prices.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
    await user.upload(screen.getByLabelText(/file/i), file)
    await user.click(screen.getByRole('button', { name: /^upload$/i }))

    expect(await screen.findByTestId('import-row-status-2')).toHaveTextContent('Matched')
    const commitButton = screen.getByRole('button', { name: /^commit$/i })
    expect(commitButton).toBeEnabled()

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ ...batch, status: 'Committed' }), { status: 200 })) // POST commit
    await user.click(commitButton)

    await waitFor(() => {
      const commitCall = fetchMock.mock.calls.find(([url]) => url === `/pricing/imports/${batch.id}/commit`)
      expect(commitCall).toBeDefined()
    })
  })

  it('rejects a staged batch through the endpoint', async () => {
    loadOnce([defaultPriceList])

    const user = userEvent.setup()
    render(<PriceListsScreen />)
    await screen.findByText('Default')

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([mapping]), { status: 200 }))
    await user.click(screen.getByRole('button', { name: /^import$/i }))
    await screen.findByLabelText(/supplier/i)

    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify(batch), { status: 201 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(batchDetail), { status: 200 }))

    const file = new File(['dummy'], 'prices.xlsx', { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
    await user.upload(screen.getByLabelText(/file/i), file)
    await user.click(screen.getByRole('button', { name: /^upload$/i }))

    await screen.findByTestId('import-row-status-2')

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ ...batch, status: 'Rejected' }), { status: 200 }))
    await user.click(screen.getByRole('button', { name: /^reject$/i }))

    expect(await screen.findByText(/batch rejected\./i)).toBeInTheDocument()
    const rejectCall = fetchMock.mock.calls.find(([url]) => url === `/pricing/imports/${batch.id}/reject`)
    expect(rejectCall).toBeDefined()
  })

  // ---- T4c: the shared data-view layer ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    loadOnce([defaultPriceList])

    const { container } = render(<PriceListsScreen />)

    await screen.findByText('Default')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"]')).toBeNull()
  })

  it('renders the real price list columns for each listed record', async () => {
    loadOnce([seasonalPriceList], [])

    render(<PriceListsScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Seasonal')).toBeInTheDocument()
    // `isDefault` is rendered as its own column, not inferred from the name.
    expect(within(row).getByText('No')).toBeInTheDocument()
    expect(within(row).getByText(new Date('2024-03-05T00:00:00Z').toLocaleDateString('es-AR'))).toBeInTheDocument()
  })

  it('shows an empty state when the organization has no price list at all', async () => {
    loadOnce([], [])

    render(<PriceListsScreen />)

    expect(await screen.findByText('No price lists yet.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('does not claim there are no price lists when the load failed', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    render(<PriceListsScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/unreachable/i)
    // "No price lists yet." is a real rendering of this screen (see the empty
    // state case above), so its absence here is a fact about this state.
    expect(screen.queryAllByText('No price lists yet.')).toHaveLength(0)
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/price lists could not be loaded/i)
  })

  it('keeps a failed action from being reported as a failed load', async () => {
    loadOnce([], [])
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ title: 'A default price list already exists.' }), { status: 409 }),
    ) // create default

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByText('No price lists yet.')
    await user.click(screen.getByRole('button', { name: /create default price list/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/a default price list already exists/i)
    // The list itself was read fine — it is genuinely empty, not unreadable.
    expect(screen.queryAllByTestId('data-view-load-error')).toHaveLength(0)
    expect(screen.getByText('No price lists yet.')).toBeInTheDocument()
  })

  it('filters the listed price lists client-side by name', async () => {
    loadOnce([defaultPriceList, seasonalPriceList], [])

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByText('Seasonal')
    expect(screen.getAllByText('Default')).toHaveLength(1)

    await user.type(screen.getByLabelText(/search price lists/i), 'seasonal')

    expect(screen.getByText('Seasonal')).toBeInTheDocument()
    expect(screen.queryAllByText('Default')).toHaveLength(0)
    // Client-side: still exactly the two mount-time reads.
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    loadOnce([defaultPriceList, seasonalPriceList], [])

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    await screen.findByText('Seasonal')
    await user.type(screen.getByLabelText(/search price lists/i), 'zzzz')

    expect(screen.getByText(/no price lists match/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    loadOnce([defaultPriceList, seasonalPriceList], [])
    loadOnce([defaultPriceList, seasonalPriceList], [])

    const user = userEvent.setup()
    const first = render(<PriceListsScreen />)

    await screen.findByText('Seasonal')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:price-lists')).toBe('cards')

    first.unmount()
    render(<PriceListsScreen />)

    await screen.findByText('Seasonal')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('keeps the view preference separate from the other data screens', async () => {
    window.localStorage.setItem('view:catalog', 'cards')
    loadOnce([defaultPriceList], [])

    render(<PriceListsScreen />)

    await screen.findByText('Default')
    // Price lists reads `view:price-lists`, which is unset here.
    expect(screen.getByRole('table')).toBeInTheDocument()
    await waitFor(() => expect(window.localStorage.getItem('view:price-lists')).toBeNull())
  })

  it('does not open any price list until "Manage prices" is clicked', async () => {
    loadOnce([defaultPriceList, seasonalPriceList])

    render(<PriceListsScreen />)

    await screen.findByText('Default')
    expect(screen.queryByTestId('price-list-entries')).not.toBeInTheDocument()
    expect(screen.getByRole('table')).toBeInTheDocument()
  })

  it('opens the default list\'s prices as a full-screen page, replacing the list', async () => {
    loadOnce([defaultPriceList, seasonalPriceList])

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    const row = within(await screen.findByRole('table'))
      .getAllByRole('row')
      .find((candidate) => within(candidate).queryByText('Default') !== null)
    expect(row).toBeDefined()
    await user.click(within(row!).getByRole('button', { name: /manage prices/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: /prices in default/i })).toBeInTheDocument()
    const prices = screen.getByTestId('price-list-entries')
    expect(within(prices).getByText('1.5L bottle')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /back to price lists/i }))

    expect(screen.getByText('Default')).toBeInTheDocument()
    expect(screen.getByText('Seasonal')).toBeInTheDocument()
  })

  it('publishes against the price list the operator picked, not the default one', async () => {
    loadOnce([defaultPriceList, seasonalPriceList])
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          id: '55555555-5555-5555-5555-555555555555',
          organizationId: 'org-1',
          priceListId: seasonalPriceList.id,
          presentationId: presentation.id,
          unitPrice: 720,
          effectiveFrom: '2024-08-01',
          source: 'Manual',
          importBatchId: null,
          createdAtUtc: '2024-08-01T00:00:00Z',
          createdByUserId: 'user-1',
        }),
        { status: 201 },
      ),
    )

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    const row = within(await screen.findByRole('table'))
      .getAllByRole('row')
      .find((candidate) => within(candidate).queryByText('Seasonal') !== null)
    expect(row).toBeDefined()
    await user.click(within(row!).getByRole('button', { name: /manage prices/i }))

    expect(screen.getByRole('heading', { name: /prices in seasonal/i })).toBeInTheDocument()
    const prices = screen.getByTestId('price-list-entries')
    await user.click(within(prices).getByRole('button', { name: /new price/i }))
    await user.type(screen.getByLabelText('Unit price'), '720')
    await user.type(screen.getByLabelText('Effective from'), '2024-08-01')
    await user.click(screen.getByRole('button', { name: /^publish$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(fetchMock.mock.calls[2][0]).toBe(`/pricing/price-lists/${seasonalPriceList.id}/entries`)
  })

  it('reads the history of the managed price list', async () => {
    loadOnce([defaultPriceList, seasonalPriceList])

    const user = userEvent.setup()
    render(<PriceListsScreen />)

    const row = within(await screen.findByRole('table'))
      .getAllByRole('row')
      .find((candidate) => within(candidate).queryByText('Default') !== null)
    expect(row).toBeDefined()
    await user.click(within(row!).getByRole('button', { name: /manage prices/i }))
    const prices = await screen.findByTestId('price-list-entries')

    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify([
          {
            id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
            organizationId: 'org-1',
            priceListId: defaultPriceList.id,
            presentationId: presentation.id,
            unitPrice: 540.5,
            effectiveFrom: '2024-05-01',
            source: 'Manual',
            importBatchId: null,
            createdAtUtc: '2024-05-01T00:00:00Z',
            createdByUserId: 'user-1',
          },
        ]),
        { status: 200 },
      ),
    )
    await user.click(within(prices).getByRole('button', { name: /^history$/i }))

    expect(await screen.findByText('2024-05-01: $540.50')).toBeInTheDocument()
    expect(fetchMock.mock.calls[2][0]).toBe(
      `/pricing/price-lists/${defaultPriceList.id}/presentations/${presentation.id}/history`,
    )
  })
})
