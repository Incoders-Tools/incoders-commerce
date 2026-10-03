import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import { CatalogScreen } from './CatalogScreen'
import { QuantityBehavior } from '@/api/types'
import type { CategoryRecord, PresentationRecord, ProductRecord } from '@/api/types'

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
    branchId: 'branch-1',
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

  // catalog-categories: the edit form also reads the product and the
  // organization's categories, so the edit tests route by method + URL
  // instead of relying on call order.
  const product: ProductRecord = {
    id: unlabelled.productId,
    organizationId: 'org-1',
    branchId: 'branch-1',
    name: 'Soda',
    categoryId: 'cat-1',
    defaultUnitId: unlabelled.unitId,
    createdAtUtc: '2024-01-01T00:00:00Z',
    createdByUserId: 'user-1',
    isActive: true,
    deactivatedAtUtc: null,
    updatedAtUtc: '2024-01-01T00:00:00Z',
  }
  const meat: CategoryRecord = {
    id: 'cat-1',
    organizationId: 'org-1',
    name: 'Carnes',
    iconKey: 'meat',
    createdAtUtc: '2024-01-01T00:00:00Z',
    updatedAtUtc: '2024-01-01T00:00:00Z',
  }
  const wine: CategoryRecord = { ...meat, id: 'cat-2', name: 'Vinos', iconKey: 'wine' }

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  function routeFetch(routes: Record<string, () => Response>) {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const handler = routes[`${init?.method ?? 'GET'} ${url}`]
      if (!handler) throw new TypeError(`unrouted ${init?.method ?? 'GET'} ${url}`)
      return handler()
    })
  }

  const callsTo = (key: string) =>
    fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${url}` === key)

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
    expect(screen.getByText('Sin código')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe('/catalog/presentations')
  })

  it("lets an admin set a Presentation's identification code via PUT", async () => {
    routeFetch({
      'GET /catalog/presentations': () => json([unlabelled]),
      'GET /catalog/products': () => json([product]),
      'GET /catalog/categories': () => json([meat, wine]),
      [`PUT /catalog/presentations/${unlabelled.id}`]: () =>
        json({ ...unlabelled, identificationCode: '7791234567890' }),
    })

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /editar código/i }))
    await user.type(screen.getByLabelText(/código de identificación/i), '7791234567890')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(callsTo(`PUT /catalog/presentations/${unlabelled.id}`)).toHaveLength(1))
    // The category was not touched, so the product is left alone.
    expect(callsTo(`PUT /catalog/products/${product.id}/category`)).toHaveLength(0)
    const init = callsTo(`PUT /catalog/presentations/${unlabelled.id}`)[0][1]
    expect(JSON.parse(init.body as string)).toMatchObject({
      name: unlabelled.name,
      quantityBehavior: unlabelled.quantityBehavior,
      unitId: unlabelled.unitId,
      identificationCode: '7791234567890',
    })

    await screen.findByText('7791234567890')
    expect(screen.queryByText('Sin código')).not.toBeInTheDocument()
  })

  it("edits the product's category from the same page, preselecting the current one", async () => {
    routeFetch({
      'GET /catalog/presentations': () => json([unlabelled]),
      'GET /catalog/products': () => json([product]),
      'GET /catalog/categories': () => json([meat, wine]),
      [`PUT /catalog/presentations/${unlabelled.id}`]: () => json(unlabelled),
      [`PUT /catalog/products/${product.id}/category`]: () => json({ ...product, categoryId: 'cat-2' }),
    })

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /editar código/i }))

    const select = await screen.findByLabelText('Categoría')
    await waitFor(() => expect(select).toHaveValue('cat-1'))
    expect(within(select).getAllByRole('option').map((option) => option.textContent)).toEqual(['Carnes', 'Vinos'])

    await user.selectOptions(select, 'cat-2')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(callsTo(`PUT /catalog/products/${product.id}/category`)).toHaveLength(1))
    const init = callsTo(`PUT /catalog/products/${product.id}/category`)[0][1]
    expect(JSON.parse(init.body as string)).toEqual({ categoryId: 'cat-2' })
  })

  it('still saves the identification code when the categories cannot be loaded', async () => {
    routeFetch({
      'GET /catalog/presentations': () => json([unlabelled]),
      [`PUT /catalog/presentations/${unlabelled.id}`]: () =>
        json({ ...unlabelled, identificationCode: '7791234567890' }),
    })

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /editar código/i }))
    await user.type(screen.getByLabelText(/código de identificación/i), '7791234567890')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await screen.findByText('7791234567890')
    expect(screen.queryByLabelText('Categoría')).not.toBeInTheDocument()
  })

  it('surfaces a visible error state when the API is unreachable, never stale/mock data', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<CatalogScreen />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/no está disponible/i)
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

    expect(await screen.findByText(/no hay presentaciones/i)).toBeInTheDocument()
  })

  it('filters the listed presentations client-side by name or identification code', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByText('330ml can')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/buscar presentaciones/i), '330')

    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
    // Filtering is purely client-side over what was already loaded.
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.clear(screen.getByLabelText(/buscar presentaciones/i))
    await user.type(screen.getByLabelText(/buscar presentaciones/i), '7790000000001')

    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.type(screen.getByLabelText(/buscar presentaciones/i), 'zzzz')

    expect(screen.getByText(/ninguna presentación coincide/i)).toBeInTheDocument()
  })

  it('switches to the card view and restores that preference on remount', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([unlabelled]), { status: 200 }))

    const user = userEvent.setup()
    const first = render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)
    expect(window.localStorage.getItem('view:catalog')).toBe('cards')

    first.unmount()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still edits the identification code from the card view', async () => {
    window.localStorage.setItem('view:catalog', 'cards')
    routeFetch({
      'GET /catalog/presentations': () => json([unlabelled]),
      'GET /catalog/products': () => json([product]),
      'GET /catalog/categories': () => json([meat, wine]),
      [`PUT /catalog/presentations/${unlabelled.id}`]: () =>
        json({ ...unlabelled, identificationCode: '7791234567890' }),
    })

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('button', { name: /editar código/i }))
    await user.type(screen.getByLabelText(/código de identificación/i), '7791234567890')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await screen.findByText('7791234567890')
  })

  it('replaces the list with a full-screen edit page instead of expanding the row inline', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getAllByRole('button', { name: /editar código/i })[0])

    // The list (and the other presentation's row) is gone, not just a form
    // appended under this row.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByText('330ml can')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Editar presentación' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /volver al catálogo/i }))

    expect(screen.getByText('1.5L bottle')).toBeInTheDocument()
    expect(screen.getByText('330ml can')).toBeInTheDocument()
  })

  it('keeps the search text and view preference after returning from the edit page', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([unlabelled, labelled]), { status: 200 }))

    const user = userEvent.setup()
    render(<CatalogScreen />)

    await screen.findByText('1.5L bottle')
    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))
    await user.type(screen.getByLabelText(/buscar presentaciones/i), '330')
    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /editar código/i }))
    await user.click(screen.getByRole('button', { name: /volver al catálogo/i }))

    expect(screen.getByLabelText(/buscar presentaciones/i)).toHaveValue('330')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByText('330ml can')).toBeInTheDocument()
    expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
  })

  // vaca-verde T1: product soft deletion. Inactive products leave the default list; the "Estado" filter brings them back.
  describe('soft deletion', () => {
    const inactiveProduct: ProductRecord = { ...product, id: '55555555-5555-5555-5555-555555555555', name: 'Retirado', isActive: false }
    const retired: PresentationRecord = { ...labelled, id: '66666666-6666-6666-6666-666666666666', productId: inactiveProduct.id, name: 'Corte retirado' }
    const activeProduct: ProductRecord = { ...product }

    it('shows only active products by default and asks the API for no inactive ones', async () => {
      routeFetch({ 'GET /catalog/presentations': () => json([unlabelled]) })

      render(<CatalogScreen />)

      await screen.findByText('1.5L bottle')
      expect(callsTo('GET /catalog/presentations')).toHaveLength(1)
      expect(screen.getByLabelText('Estado')).toHaveValue('active')
      expect(screen.queryByText('Inactivo')).not.toBeInTheDocument()
    })

    it('the Inactivos filter lists only inactive products, flagged with an Inactivo badge', async () => {
      routeFetch({
        'GET /catalog/presentations': () => json([unlabelled]),
        'GET /catalog/presentations?includeInactive=true': () => json([unlabelled, retired]),
        'GET /catalog/products?includeInactive=true': () => json([activeProduct, inactiveProduct]),
      })

      const user = userEvent.setup()
      render(<CatalogScreen />)
      await screen.findByText('1.5L bottle')

      await user.selectOptions(screen.getByLabelText('Estado'), 'inactive')

      await screen.findByText('Corte retirado')
      expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument()
      expect(screen.getByText('Inactivo')).toBeInTheDocument()

      await user.selectOptions(screen.getByLabelText('Estado'), 'all')
      expect(await screen.findByText('1.5L bottle')).toBeInTheDocument()
      expect(screen.getByText('Corte retirado')).toBeInTheDocument()
    })

    it('deactivating asks for confirmation, then posts and removes the product from the default list', async () => {
      routeFetch({
        'GET /catalog/presentations': () => json([unlabelled, labelled]),
        [`POST /catalog/products/${product.id}/deactivate`]: () => json({ ...product, isActive: false }),
      })

      const user = userEvent.setup()
      render(<CatalogScreen />)
      await screen.findByText('1.5L bottle')

      await user.click(screen.getAllByRole('button', { name: /^desactivar$/i })[0])
      expect(screen.getByText(/el producto deja de aparecer en el pos y en las listas; su historial se conserva/i)).toBeInTheDocument()
      expect(callsTo(`POST /catalog/products/${product.id}/deactivate`)).toHaveLength(0)

      await user.click(screen.getByRole('button', { name: /^desactivar producto$/i }))

      await waitFor(() => expect(callsTo(`POST /catalog/products/${product.id}/deactivate`)).toHaveLength(1))
      // Both presentations belong to the same product, so both leave the active list.
      await waitFor(() => expect(screen.queryByText('1.5L bottle')).not.toBeInTheDocument())
      expect(screen.queryByText('330ml can')).not.toBeInTheDocument()
    })

    it('cancelling the confirmation changes nothing', async () => {
      routeFetch({ 'GET /catalog/presentations': () => json([unlabelled]) })

      const user = userEvent.setup()
      render(<CatalogScreen />)
      await screen.findByText('1.5L bottle')

      await user.click(screen.getByRole('button', { name: /^desactivar$/i }))
      await user.click(screen.getByRole('button', { name: /^cancelar$/i }))

      expect(screen.getByText('1.5L bottle')).toBeInTheDocument()
      expect(fetchMock).toHaveBeenCalledTimes(1)
    })

    it('reactivating an inactive product posts and clears its badge', async () => {
      routeFetch({
        'GET /catalog/presentations': () => json([unlabelled]),
        'GET /catalog/presentations?includeInactive=true': () => json([retired]),
        'GET /catalog/products?includeInactive=true': () => json([inactiveProduct]),
        [`POST /catalog/products/${inactiveProduct.id}/reactivate`]: () => json({ ...inactiveProduct, isActive: true }),
      })

      const user = userEvent.setup()
      render(<CatalogScreen />)
      await screen.findByText('1.5L bottle')
      await user.selectOptions(screen.getByLabelText('Estado'), 'all')
      await screen.findByText('Corte retirado')

      await user.click(screen.getByRole('button', { name: /^reactivar$/i }))

      await waitFor(() => expect(callsTo(`POST /catalog/products/${inactiveProduct.id}/reactivate`)).toHaveLength(1))
      await waitFor(() => expect(screen.queryByText('Inactivo')).not.toBeInTheDocument())
    })
  })

  it('asks for a branch instead of loading when none is selected', async () => {
    render(
      <BranchContext.Provider value={{ selectedBranch: null, selectableBranches: [], selectBranch: () => {} }}>
        <CatalogScreen />
      </BranchContext.Provider>,
    )

    expect(await screen.findByRole('status')).toHaveTextContent(/Elegí una sucursal/)
    expect(screen.getByRole('heading', { name: 'Catálogo' })).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })
})
