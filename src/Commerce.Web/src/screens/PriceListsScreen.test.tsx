import { render, screen, waitFor } from '@testing-library/react'
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
  })

  it('loads the default price list and presentations, and lists them under Prices', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([defaultPriceList]), { status: 200 })) // GET /pricing/price-lists
      .mockResolvedValueOnce(new Response(JSON.stringify([presentation]), { status: 200 })) // GET /catalog/presentations

    render(<PriceListsScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByText('7791234567890')).toBeInTheDocument()
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

    await screen.findByText('1.5L bottle')
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
    await screen.findByText('1.5L bottle')

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
})
