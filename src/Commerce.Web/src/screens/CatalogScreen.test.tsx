import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CatalogScreen } from './CatalogScreen'

/**
 * Proves the catalog screen calls Cloud.Api's real HTTP route/shape
 * (Endpoints/Catalog.cs `POST /catalog/products/{productId}/rename`) rather
 * than depending on hardcoded/mocked data (spec.md "Web SPA Consumes Real
 * HTTP Endpoints"). `fetch` is mocked at the transport boundary only — the
 * request URL, method and body shape asserted here are the real contract.
 */
describe('CatalogScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('Product ID'), '11111111-1111-1111-1111-111111111111')
    await user.type(screen.getByLabelText('Target branch ID'), '33333333-3333-3333-3333-333333333333')
    await user.type(screen.getByLabelText('Current name'), 'Old Name')
    await user.type(screen.getByLabelText('Category ID'), '44444444-4444-4444-4444-444444444444')
    await user.type(screen.getByLabelText('Default unit ID'), '55555555-5555-5555-5555-555555555555')
    await user.type(screen.getByLabelText('New name'), 'New Name')
    await user.click(screen.getByRole('button', { name: /rename product/i }))
  }

  it('POSTs to the real /catalog/products/{id}/rename route with the exact request shape', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          // Real Cloud.Api has no JsonStringEnumConverter, so this enum
          // serializes as its numeric ordinal (0 = Allowed) — see
          // api/types.ts's ManagementOutcomeStatus remarks.
          status: 0,
          reason: 'allowed',
          updatedProduct: { id: '1', organizationId: '1', name: 'New Name', categoryId: '1', defaultUnitId: '1' },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )

    const user = userEvent.setup()
    render(<CatalogScreen />)
    await fillAndSubmit(user)

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1))

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/catalog/products/11111111-1111-1111-1111-111111111111/rename')
    expect(init.method).toBe('POST')
    expect(init.credentials).toBe('include')

    const body = JSON.parse(init.body as string)
    expect(body).toMatchObject({
      targetBranchId: '33333333-3333-3333-3333-333333333333',
      currentName: 'Old Name',
      categoryId: '44444444-4444-4444-4444-444444444444',
      defaultUnitId: '55555555-5555-5555-5555-555555555555',
      newName: 'New Name',
      isOffline: false,
    })
    // Privilege-escalation regression guard: the request body must carry NO
    // actor identity/permission fields — the server loads the actor from the
    // authenticated session, never from the request.
    expect(body).not.toHaveProperty('actorId')
    expect(body).not.toHaveProperty('actorBranchScope')
    expect(body).not.toHaveProperty('actorRoles')

    await screen.findByTestId('catalog-outcome')
    expect(screen.getByTestId('catalog-outcome')).toHaveTextContent('Renamed to "New Name"')
  })

  it('surfaces a visible error state when the API is unreachable, never stale/mock data', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const user = userEvent.setup()
    render(<CatalogScreen />)
    await fillAndSubmit(user)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/unreachable/i)
    expect(screen.queryByTestId('catalog-outcome')).not.toBeInTheDocument()
  })
})
