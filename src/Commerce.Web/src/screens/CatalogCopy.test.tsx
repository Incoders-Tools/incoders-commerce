import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CatalogScreen } from './CatalogScreen'
import { AuthContext } from '@/auth/AuthContext'
import { BranchContext } from '@/branch/BranchContext'
import { Permission, QuantityBehavior } from '@/api/types'
import type { PresentationRecord, ProductRecord, SignedInResponse } from '@/api/types'

/**
 * B7 U5b (catalog-item-identification spec, "Copying Catalog Between
 * Branches"): the admin-only "Copy catalog" action on the branch-scoped
 * Catalog screen. The server stays the authority (403 for non-admins,
 * cross-organization targets, and so on); these tests pin the UI contract.
 */
describe('CatalogScreen — copy catalog to another branch', () => {
  const fetchMock = vi.fn()

  const rutaId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
  const centroId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
  const norteId = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
  const branches = [
    { id: rutaId, name: 'Ruta 51' },
    { id: centroId, name: 'Centro' },
    { id: norteId, name: 'Norte' },
  ]

  const presentation: PresentationRecord = {
    id: '11111111-1111-1111-1111-111111111111',
    organizationId: 'org-1',
    branchId: rutaId,
    productId: '22222222-2222-2222-2222-222222222222',
    name: '1.5L bottle',
    quantityBehavior: QuantityBehavior.FixedQuantity,
    unitId: '33333333-3333-3333-3333-333333333333',
    identificationCode: '7791234567890',
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
    updatedAtUtc: '2024-01-01T00:00:00Z',
  }

  const product = (id: string, name: string): ProductRecord => ({
    id,
    organizationId: 'org-1',
    branchId: rutaId,
    name,
    categoryId: 'cat',
    defaultUnitId: 'unit',
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
    updatedAtUtc: '2024-01-01T00:00:00Z',
  })
  const yerba = product('dddddddd-dddd-dddd-dddd-dddddddddddd', 'Yerba Mate')
  const cola = product('eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee', 'Cola')

  function buildUser(overrides: Partial<SignedInResponse> = {}): SignedInResponse {
    return {
      userId: 'user-1',
      organizationId: 'org-1',
      displayName: 'Admin',
      permissions: Permission.ManageCatalog,
      isSystemAdmin: false,
      selectableBranches: branches,
      ...overrides,
    }
  }

  function renderScreen(user: SignedInResponse, selectableBranches = branches) {
    return render(
      <AuthContext.Provider value={{ user, error: null, signIn: async () => {}, signOut: async () => {} }}>
        <BranchContext.Provider
          value={{ selectedBranch: selectableBranches[0], selectableBranches, selectBranch: () => {} }}
        >
          <CatalogScreen />
        </BranchContext.Provider>
      </AuthContext.Provider>,
    )
  }

  function respond(handlers: Record<string, () => Response>) {
    fetchMock.mockImplementation((url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      const handler = handlers[key]
      if (!handler) throw new Error(`Unexpected request: ${key}`)
      return Promise.resolve(handler())
    })
  }

  const json = (body: unknown, status = 200) => () => new Response(JSON.stringify(body), { status })

  function copyRequestBody() {
    const call = fetchMock.mock.calls.find(([url]) => url === '/catalog/copy')
    expect(call).toBeDefined()
    expect(call![1].method).toBe('POST')
    return JSON.parse(call![1].body as string)
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('offers no copy action to staff without catalog administration', async () => {
    respond({ 'GET /catalog/presentations': json([presentation]) })

    renderScreen(buildUser({ permissions: Permission.ViewSales }))

    await screen.findByText('1.5L bottle')
    expect(screen.queryByRole('button', { name: /copiar catálogo/i })).not.toBeInTheDocument()
  })

  it('offers no copy action when there is no other branch to copy to', async () => {
    respond({ 'GET /catalog/presentations': json([presentation]) })

    renderScreen(buildUser(), [branches[0]])

    await screen.findByText('1.5L bottle')
    expect(screen.queryByRole('button', { name: /copiar catálogo/i })).not.toBeInTheDocument()
  })

  it('copies the whole catalog to the chosen branch and reports copied and skipped items', async () => {
    respond({
      'GET /catalog/presentations': json([presentation]),
      'POST /catalog/copy': json({
        productsCopied: 3,
        presentationsCopied: 4,
        skipped: [
          { presentationId: 'p1', identificationCode: '7790000000001', reason: 'identification-code-in-target' },
        ],
        priceListId: 'list-1',
        priceEntriesCopied: 5,
      }),
    })

    const user = userEvent.setup()
    renderScreen(buildUser())

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /copiar catálogo/i }))

    const target = screen.getByLabelText(/sucursal de destino/i)
    // The source (currently selected) branch is never a valid target.
    expect(within(target).queryByRole('option', { name: 'Ruta 51' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^copiar$/i })).toBeDisabled()

    await user.selectOptions(target, 'Centro')
    await user.click(screen.getByRole('button', { name: /^copiar$/i }))

    await screen.findByText(/productos copiados: 3/i)
    expect(copyRequestBody()).toEqual({ sourceBranchId: rutaId, targetBranchId: centroId })
    expect(screen.getByText(/presentaciones copiadas: 4/i)).toBeInTheDocument()
    expect(screen.getByText(/precios copiados: 5/i)).toBeInTheDocument()
    expect(screen.getByText(/presentaciones omitidas: 1/i)).toBeInTheDocument()
    expect(screen.getByText('7790000000001')).toBeInTheDocument()
  })

  it('copies only the selected products', async () => {
    respond({
      'GET /catalog/presentations': json([presentation]),
      'GET /catalog/products': json([yerba, cola]),
      'POST /catalog/copy': json({
        productsCopied: 1,
        presentationsCopied: 1,
        skipped: [],
        priceListId: null,
        priceEntriesCopied: 0,
      }),
    })

    const user = userEvent.setup()
    renderScreen(buildUser())

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /copiar catálogo/i }))
    await user.selectOptions(screen.getByLabelText(/sucursal de destino/i), 'Norte')
    await user.click(screen.getByRole('radio', { name: /productos seleccionados/i }))

    // Nothing selected yet: submitting would silently copy nothing.
    await screen.findByRole('checkbox', { name: 'Yerba Mate' })
    expect(screen.getByRole('button', { name: /^copiar$/i })).toBeDisabled()

    await user.click(screen.getByRole('checkbox', { name: 'Yerba Mate' }))
    await user.click(screen.getByRole('button', { name: /^copiar$/i }))

    await screen.findByText(/productos copiados: 1/i)
    expect(copyRequestBody()).toEqual({
      sourceBranchId: rutaId,
      targetBranchId: norteId,
      productIds: [yerba.id],
    })
    expect(screen.getByText(/no se copió ninguna lista de precios/i)).toBeInTheDocument()
  })

  it('shows a visible error when the copy is refused', async () => {
    respond({
      'GET /catalog/presentations': json([presentation]),
      'POST /catalog/copy': json({ error: 'nope' }, 403),
    })

    const user = userEvent.setup()
    renderScreen(buildUser())

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /copiar catálogo/i }))
    await user.selectOptions(screen.getByLabelText(/sucursal de destino/i), 'Centro')
    await user.click(screen.getByRole('button', { name: /^copiar$/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/no se pudo copiar/i)
    await waitFor(() => expect(screen.queryByText(/productos copiados/i)).not.toBeInTheDocument())
  })

  it('returns to the catalog list from the copy page', async () => {
    respond({ 'GET /catalog/presentations': json([presentation]) })

    const user = userEvent.setup()
    renderScreen(buildUser())

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /copiar catálogo/i }))
    await user.click(screen.getByRole('button', { name: /volver al catálogo/i }))

    expect(await screen.findByText('1.5L bottle')).toBeInTheDocument()
  })
})
