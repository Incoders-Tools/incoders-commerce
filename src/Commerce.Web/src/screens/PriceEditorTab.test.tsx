import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PriceListsScreen } from './PriceListsScreen'
import { UnsavedChangesProvider, useUnsavedChangesGuard } from '@/components/layout/UnsavedChanges'
import type {
  BreakdownItem,
  CategoryRecord,
  PresentationRecord,
  PriceListBreakdown,
  PriceListRecord,
  ProductRecord,
} from '@/api/types'

const list = (id: string, name: string, isDefault: boolean): PriceListRecord => ({
  id,
  organizationId: 'org-1',
  branchId: 'branch-1',
  name,
  isDefault,
  createdAtUtc: '2026-01-01T00:00:00Z',
  createdByUserId: 'user-1',
})

const minorista = list('list-minorista', 'Minorista', true)
const mayorista = list('list-mayorista', 'Mayorista', false)

const category = (id: string, name: string): CategoryRecord => ({
  id,
  organizationId: 'org-1',
  name,
  iconKey: 'tag',
  createdAtUtc: '2026-01-01T00:00:00Z',
  updatedAtUtc: '2026-01-01T00:00:00Z',
})

const product = (id: string, name: string, categoryId: string): ProductRecord => ({
  id,
  organizationId: 'org-1',
  branchId: 'branch-1',
  name,
  categoryId,
  defaultUnitId: 'unit-kg',
  createdAtUtc: '2026-01-01T00:00:00Z',
  createdByUserId: 'user-1',
  updatedAtUtc: '2026-01-01T00:00:00Z',
  isActive: true,
  deactivatedAtUtc: null,
})

const presentation = (id: string, productId: string, name: string, code: string | null): PresentationRecord => ({
  id,
  organizationId: 'org-1',
  branchId: 'branch-1',
  productId,
  name,
  quantityBehavior: 1,
  unitId: 'unit-kg',
  identificationCode: code,
  createdAtUtc: '2026-01-01T00:00:00Z',
  createdByUserId: 'user-1',
  updatedAtUtc: '2026-01-01T00:00:00Z',
})

const categories = [category('cat-carnes', 'Carnes'), category('cat-lacteos', 'Lácteos')]
const products = [
  product('prod-lengua', 'Lengua', 'cat-carnes'),
  product('prod-vacio', 'Vacío', 'cat-carnes'),
  product('prod-queso', 'Queso', 'cat-lacteos'),
]
const presentations = [
  presentation('pres-lengua', 'prod-lengua', 'Por kg', '100'),
  presentation('pres-vacio', 'prod-vacio', 'Por kg', '200'),
  presentation('pres-queso-kg', 'prod-queso', 'Por kg', '300'),
  presentation('pres-queso-horma', 'prod-queso', 'Horma', '301'),
]

const item = (presentationId: string, productName: string, presentationName: string, base: number, final: number): BreakdownItem => ({
  presentationId,
  productId: '',
  productName,
  presentationName,
  identificationCode: null,
  entryEffectiveFrom: '2026-09-01',
  base,
  components: [],
  final,
})

const breakdownOf = (priceList: PriceListRecord, items: BreakdownItem[]): PriceListBreakdown => ({
  priceListId: priceList.id,
  priceListName: priceList.name,
  on: '2026-10-04',
  floorPriceListId: null,
  composition: { source: 'none', effectiveFrom: null, components: [], history: [] },
  items,
})

// The Horma has no price yet: it is listed, but has nothing to remark.
const minoristaItems = [
  item('pres-lengua', 'Lengua', 'Por kg', 1000, 1210),
  item('pres-vacio', 'Vacío', 'Por kg', 2500.55, 3025.67),
  item('pres-queso-kg', 'Queso', 'Por kg', 800.05, 968.06),
]

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

type Handler = (url: string, init?: RequestInit) => Response | Promise<Response> | undefined

// The prices already published for 2026-10-20: Lengua goes to 1100 that day.
const futureItems = [
  item('pres-lengua', 'Lengua', 'Por kg', 1100, 1331),
  item('pres-vacio', 'Vacío', 'Por kg', 2500.55, 3025.67),
  item('pres-queso-kg', 'Queso', 'Por kg', 800.05, 968.06),
]

describe('PriceListsScreen "Editar precios" tab', () => {
  const fetchMock = vi.fn()
  let batchHandler: Handler
  let breakdownItems: BreakdownItem[]
  // Overrides the breakdown of Minorista on 2026-10-20 (to hold or fail it); undefined answers futureItems.
  let futureBreakdownHandler: (() => Response | Promise<Response>) | undefined

  beforeEach(() => {
    breakdownItems = minoristaItems
    futureBreakdownHandler = undefined
    batchHandler = () => json({ published: 0, entries: [] })
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/pricing/price-lists') return json([minorista, mayorista])
      if (url.startsWith('/catalog/presentations')) return json(presentations)
      if (url.startsWith('/catalog/products')) return json(products)
      if (url.startsWith('/catalog/categories')) return json(categories)
      if (url.endsWith('/entries/batch')) return batchHandler(url, init) ?? json({}, 500)
      // Every list composes with IVA 21 % on the base: 1000 -> 1210.
      if (/^\/pricing\/price-lists\/[^/]+\/composition/.test(url)) {
        return json({
          source: 'list',
          effectiveFrom: '2026-09-01',
          components: [{ code: 'IVA', label: 'IVA', percentage: 21, calculationBase: 'Base', order: 1 }],
          history: [],
        })
      }
      const breakdown = url.match(/^\/pricing\/price-lists\/([^/]+)\/breakdown/)
      if (breakdown) {
        const owner = breakdown[1] === mayorista.id ? mayorista : minorista
        if (owner === minorista && url.endsWith('?on=2026-10-20')) {
          if (futureBreakdownHandler) return futureBreakdownHandler()
          return json({ ...breakdownOf(owner, futureItems), on: '2026-10-20' })
        }
        return json(breakdownOf(owner, owner === mayorista ? [item('pres-lengua', 'Lengua', 'Por kg', 900, 1089)] : breakdownItems))
      }
      if (url.includes('/history')) {
        return json([
          {
            id: 'entry-1',
            organizationId: 'org-1',
            branchId: 'branch-1',
            priceListId: minorista.id,
            presentationId: 'pres-lengua',
            unitPrice: 950,
            effectiveFrom: '2026-08-01',
            source: 'Manual',
            importBatchId: null,
            createdAtUtc: '2026-08-01T00:00:00Z',
            createdByUserId: 'user-1',
          },
        ])
      }
      return json({ title: `unexpected ${url}` }, 404)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const batchCalls = () => fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/entries/batch'))
  const breakdownCalls = (listId: string) =>
    fetchMock.mock.calls.filter(([url]) => String(url).startsWith(`/pricing/price-lists/${listId}/breakdown`))

  const rowOf = (name: string | RegExp) => {
    const row = within(screen.getByTestId('price-editor-grid'))
      .getAllByRole('row')
      .find((candidate) => within(candidate).queryByText(name) !== null)
    if (!row) throw new Error(`no row for ${String(name)}`)
    return row
  }

  // What the app-wide unsaved changes guard currently holds.
  function GuardProbe() {
    return <output data-testid="app-guard">{useUnsavedChangesGuard().message ?? ''}</output>
  }

  async function openEditor({ withAppGuard = false } = {}) {
    const user = userEvent.setup()
    render(
      withAppGuard ? (
        <UnsavedChangesProvider>
          <GuardProbe />
          <PriceListsScreen />
        </UnsavedChangesProvider>
      ) : (
        <PriceListsScreen />
      ),
    )
    await screen.findByText('Minorista')
    await user.click(screen.getByRole('button', { name: /^editar precios$/i }))
    await screen.findByText('Lengua')
    return user
  }

  it('"Gestionar precios" opens the editor tab with that list preselected', async () => {
    const user = userEvent.setup()
    render(<PriceListsScreen />)

    const row = within(await screen.findByRole('table'))
      .getAllByRole('row')
      .find((candidate) => within(candidate).queryByText('Mayorista') !== null)
    await user.click(within(row!).getByRole('button', { name: /gestionar precios/i }))

    expect(screen.getByRole('button', { name: /^editar precios$/i })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByLabelText('Lista de precios')).toHaveValue(mayorista.id)
    await screen.findByText('Lengua')
    expect(breakdownCalls(mayorista.id)).toHaveLength(1)
    expect(breakdownCalls(minorista.id)).toHaveLength(0)
    // The old nested detail page is gone.
    expect(screen.queryByTestId('price-list-entries')).not.toBeInTheDocument()
  })

  it('opens on the default list and lists products by name with code, category and current prices', async () => {
    await openEditor()

    expect(screen.getByLabelText('Lista de precios')).toHaveValue(minorista.id)
    const lengua = rowOf('Lengua')
    expect(within(lengua).getByText('100')).toBeInTheDocument()
    expect(within(lengua).getByText('Carnes')).toBeInTheDocument()
    expect(within(lengua).getByText('$ 1.000,00')).toBeInTheDocument()
    expect(within(lengua).getByText('$ 1.210,00')).toBeInTheDocument()
    // "Por kg" is the only presentation of Lengua: it adds nothing.
    expect(within(lengua).queryByText('Por kg')).not.toBeInTheDocument()
    // Queso has two presentations, so each row says which one.
    expect(within(rowOf('Horma')).getByText('Queso')).toBeInTheDocument()
    expect(within(rowOf('301')).getByText('Horma')).toBeInTheDocument()
    expect(within(rowOf('300')).getByText('Por kg')).toBeInTheDocument()
  })

  it('searches by product or code and filters by category', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Buscar productos'), '200')
    expect(screen.getByText('Vacío')).toBeInTheDocument()
    expect(screen.queryByText('Lengua')).not.toBeInTheDocument()

    await user.clear(screen.getByLabelText('Buscar productos'))
    await user.selectOptions(screen.getByLabelText('Categoría'), 'cat-lacteos')
    expect(screen.queryByText('Lengua')).not.toBeInTheDocument()
    expect(screen.getAllByText('Queso')).toHaveLength(2)
  })

  it('marks an individually edited row as changed with old -> new and the difference', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')

    const lengua = rowOf('Lengua')
    expect(lengua).toHaveAttribute('data-changed', 'true')
    expect(within(lengua).getByText('$ 1.000,00 → $ 1.100,00')).toBeInTheDocument()
    expect(within(lengua).getByText('+10 %')).toBeInTheDocument()
    expect(screen.getByText('1 precio modificado')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeEnabled()
  })

  it('flags an invalid value inline and blocks publishing', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), 'abc')

    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveAttribute('aria-invalid', 'true')
    expect(within(rowOf('Vacío')).getByText(/precio mayor que cero/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
  })

  it('keeps publishing disabled while nothing changed', async () => {
    const user = await openEditor()

    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
    // The same price is not a change.
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1000')
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
  })

  it('remarks only the selected rows, rounded to the cent', async () => {
    const user = await openEditor()

    await user.click(screen.getByLabelText('Seleccionar Queso · Por kg'))
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(screen.getByLabelText('Nueva base de Queso · Por kg')).toHaveValue('880,06')
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('')
    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveValue('')
  })

  it('remarks every filtered row when none is selected, and the preview stays editable', async () => {
    const user = await openEditor()

    await user.selectOptions(screen.getByLabelText('Categoría'), 'cat-carnes')
    await user.type(screen.getByLabelText('Remarcar %'), '-5')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))

    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('950,00')
    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveValue('2375,52')

    await user.selectOptions(screen.getByLabelText('Categoría'), '')
    expect(screen.getByLabelText('Nueva base de Queso · Por kg')).toHaveValue('')
    expect(screen.getByText('2 precios modificados')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('Nueva base de Lengua'))
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '960')
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('960')
  })

  it('selects every visible row at once', async () => {
    const user = await openEditor()

    await user.selectOptions(screen.getByLabelText('Categoría'), 'cat-lacteos')
    await user.click(screen.getByLabelText('Seleccionar todas las filas visibles'))

    expect(screen.getByLabelText('Seleccionar Queso · Por kg')).toBeChecked()
    expect(screen.getByLabelText('Seleccionar Queso · Horma')).toBeChecked()
    await user.selectOptions(screen.getByLabelText('Categoría'), '')
    expect(screen.getByLabelText('Seleccionar Lengua')).not.toBeChecked()
  })

  it('"Deshacer cambios" clears every pending edit', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))
    await user.click(screen.getByRole('button', { name: 'Deshacer cambios' }))

    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
  })

  it('publishes ONE batch with only the changed rows, then refreshes and clears', async () => {
    batchHandler = () => json({ published: 2, entries: [] })
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2600,5')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    expect(await screen.findByText('Se publicaron 2 precios.')).toBeInTheDocument()
    expect(batchCalls()).toHaveLength(1)
    const [url, init] = batchCalls()[0]
    expect(url).toBe(`/pricing/price-lists/${minorista.id}/entries/batch`)
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({
      // No date picked: the server's business day.
      effectiveFrom: null,
      entries: [
        { presentationId: 'pres-lengua', unitPrice: 1100 },
        { presentationId: 'pres-vacio', unitPrice: 2600.5 },
      ],
    })
    await waitFor(() => expect(breakdownCalls(minorista.id)).toHaveLength(2))
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
  })

  it('"+10 %" on all rows publishes every priced row x 1,10 with the chosen date', async () => {
    batchHandler = () => json({ published: 3, entries: [] })
    const user = await openEditor()

    await user.clear(screen.getByLabelText('Vigente desde'))
    await user.type(screen.getByLabelText('Vigente desde'), '2026-10-10')
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    await screen.findByText('Se publicaron 3 precios.')
    const body = JSON.parse((batchCalls()[0][1] as RequestInit).body as string)
    expect(body).toEqual({
      effectiveFrom: '2026-10-10',
      // Grid order: by product, then presentation.
      entries: [
        { presentationId: 'pres-lengua', unitPrice: 1100 },
        { presentationId: 'pres-queso-kg', unitPrice: 880.06 },
        { presentationId: 'pres-vacio', unitPrice: 2750.61 },
      ],
    })
  })

  it('on a floor refusal publishes nothing, keeps the edits and marks the offending rows', async () => {
    batchHandler = () =>
      json(
        {
          error: 'price-below-floor',
          violations: [
            {
              priceListId: minorista.id,
              priceListName: 'Minorista',
              floorPriceListId: mayorista.id,
              floorPriceListName: 'Mayorista',
              presentationId: 'pres-lengua',
              productId: 'prod-lengua',
              productName: 'Lengua',
              presentationName: 'Por kg',
              price: 800,
              floorPrice: 900,
            },
          ],
        },
        409,
      )
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '800')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2600')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    const lengua = rowOf('Lengua')
    expect(await within(lengua).findByText(/por debajo del piso Mayorista/i)).toBeInTheDocument()
    expect(within(rowOf('Vacío')).queryByText(/por debajo del piso/i)).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent(/no se publicó ningún precio/i)
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('800')
    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveValue('2600')
    expect(screen.queryByText(/se publicaron/i)).not.toBeInTheDocument()
    // Nothing refreshed: nothing was written.
    expect(breakdownCalls(minorista.id)).toHaveLength(1)
  })

  it('maps a validation problem on an entry to its row', async () => {
    batchHandler = () =>
      json({ title: 'One or more validation errors occurred.', errors: { 'entries[1].unitPrice': ['Too many decimals.'] } }, 400)
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2600')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    expect(await within(rowOf('Vacío')).findByText('Too many decimals.')).toBeInTheDocument()
    expect(within(rowOf('Lengua')).queryByText('Too many decimals.')).not.toBeInTheDocument()
  })

  it('opens a row history in a side panel without changing the grid rows', async () => {
    const user = await openEditor()

    const lengua = rowOf('Lengua')
    const cellsBefore = within(lengua).getAllByRole('cell').length
    const buttonsBefore = within(lengua).getAllByRole('button').length
    await user.click(within(lengua).getByRole('button', { name: 'Historial de Lengua' }))

    const panel = await screen.findByRole('dialog', { name: /historial de lengua/i })
    expect(await within(panel).findByText(/2026-08-01/)).toBeInTheDocument()
    expect(screen.getByTestId('price-editor-grid')).not.toContainElement(panel)
    expect(within(rowOf('Lengua')).getAllByRole('cell')).toHaveLength(cellsBefore)
    expect(within(rowOf('Lengua')).getAllByRole('button')).toHaveLength(buttonsBefore)
    expect(screen.queryByRole('button', { name: /ocultar historial/i })).not.toBeInTheDocument()

    await user.click(within(panel).getByRole('button', { name: 'Cerrar' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('starts with no date, meaning the business day, and sends null', async () => {
    batchHandler = () => json({ published: 1, entries: [] })
    const user = await openEditor()

    expect(screen.getByLabelText('Vigente desde')).toHaveValue('')
    expect(screen.getByText(/vacío = hoy \(día comercial\)/i)).toBeInTheDocument()
    // The current prices keep loading without a date.
    expect(breakdownCalls(minorista.id)[0][0]).toBe(`/pricing/price-lists/${minorista.id}/breakdown`)

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    await screen.findByText('Se publicó 1 precio.')
    expect(JSON.parse((batchCalls()[0][1] as RequestInit).body as string).effectiveFrom).toBeNull()
  })

  it('clearing a picked date goes back to null', async () => {
    batchHandler = () => json({ published: 1, entries: [] })
    const user = await openEditor()

    // The picked date is really in effect first (prices reload as of it), so clearing it below is a real change.
    await user.type(screen.getByLabelText('Vigente desde'), '2099-12-31')
    await waitFor(() =>
      expect(breakdownCalls(minorista.id).some(([url]) => String(url).includes('on=2099-12-31'))).toBe(true),
    )
    await user.clear(screen.getByLabelText('Vigente desde'))
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    await screen.findByText('Se publicó 1 precio.')
    expect(JSON.parse((batchCalls()[0][1] as RequestInit).body as string).effectiveFrom).toBeNull()
  })

  it('switching the list without pending edits does not ask', async () => {
    const user = await openEditor()

    await user.selectOptions(screen.getByLabelText('Lista de precios'), mayorista.id)

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await waitFor(() => expect(breakdownCalls(mayorista.id)).toHaveLength(1))
  })

  it('switching the list with pending edits asks; "Seguir editando" keeps the list and the edits', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.selectOptions(screen.getByLabelText('Lista de precios'), mayorista.id)

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Tenés 1 cambio sin publicar. ¿Descartarlo?')
    await user.click(within(dialog).getByRole('button', { name: 'Seguir editando' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Lista de precios')).toHaveValue(minorista.id)
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('1100')
    expect(breakdownCalls(mayorista.id)).toHaveLength(0)
  })

  it('switching the list with pending edits asks; "Descartar" loads that list and drops the edits', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2600')
    await user.selectOptions(screen.getByLabelText('Lista de precios'), mayorista.id)

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Tenés 2 cambios sin publicar. ¿Descartarlos?')
    await user.click(within(dialog).getByRole('button', { name: 'Descartar' }))

    await waitFor(() => expect(breakdownCalls(mayorista.id)).toHaveLength(1))
    expect(screen.getByLabelText('Lista de precios')).toHaveValue(mayorista.id)
    expect(await within(rowOf('Lengua')).findByText('$ 900,00')).toBeInTheDocument()
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('')
  })

  it('switching tab with pending edits asks; "Seguir editando" stays on the editor with the edits', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.click(screen.getByRole('button', { name: 'Precios' }))

    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Tenés 1 cambio sin publicar. ¿Descartarlo?')
    await user.click(within(dialog).getByRole('button', { name: 'Seguir editando' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /^editar precios$/i })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('1100')
  })

  it('switching tab with pending edits asks; "Descartar" switches and the editor starts clean', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.click(screen.getByRole('button', { name: 'Precios' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Descartar' }))

    expect(screen.getByRole('button', { name: 'Precios' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByTestId('price-editor-grid')).not.toBeInTheDocument()

    // Nothing is pending any more: leaving again does not ask.
    await user.click(screen.getByRole('button', { name: /^editar precios$/i }))
    await screen.findByText('Lengua')
    await user.click(screen.getByRole('button', { name: 'Precios' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Precios' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('switching tab without pending edits does not ask', async () => {
    const user = await openEditor()

    await user.click(screen.getByRole('button', { name: 'Precios' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Precios' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('locks the grid and the remark while publishing, showing "Publicando…"', async () => {
    let respond!: (response: Response) => void
    batchHandler = () => new Promise<Response>((resolve) => (respond = resolve))
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    expect(await screen.findByRole('button', { name: 'Publicando…' })).toBeDisabled()
    expect(screen.getByLabelText('Nueva base de Lengua')).toBeDisabled()
    expect(screen.getByLabelText('Nueva base de Vacío')).toBeDisabled()
    expect(screen.getByLabelText('Seleccionar Lengua')).toBeDisabled()
    expect(screen.getByLabelText('Seleccionar todas las filas visibles')).toBeDisabled()
    expect(screen.getByLabelText('Remarcar %')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeDisabled()
    expect(screen.getByLabelText('Vigente desde')).toBeDisabled()

    respond(json({ published: 1, entries: [] }))
    await screen.findByText('Se publicó 1 precio.')
    expect(screen.getByLabelText('Nueva base de Vacío')).toBeEnabled()
    expect(screen.getByLabelText('Remarcar %')).toBeEnabled()
  })

  it('on success clears only the edits that were published', async () => {
    let respond!: (response: Response) => void
    batchHandler = () => new Promise<Response>((resolve) => (respond = resolve))
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Queso · Por kg'), '900')
    // The current price again: not a change, so it is not published, but it is the operator's text.
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2500,55')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))
    await screen.findByRole('button', { name: 'Publicando…' })

    // The grid is locked while publishing, so this stands for any text that differs from what the batch
    // carried by the time it answers (the clear compares values, not rows): Queso is no longer 900.
    fireEvent.change(screen.getByLabelText('Nueva base de Queso · Por kg'), { target: { value: '950' } })
    expect(screen.getByLabelText('Nueva base de Queso · Por kg')).toHaveValue('950')
    respond(json({ published: 2, entries: [] }))

    await screen.findByText('Se publicaron 2 precios.')
    expect(JSON.parse((batchCalls()[0][1] as RequestInit).body as string).entries).toEqual([
      { presentationId: 'pres-lengua', unitPrice: 1100 },
      { presentationId: 'pres-queso-kg', unitPrice: 900 },
    ])
    // Published as typed: done.
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('')
    // Changed during the publish: 950 was never published, so it stays.
    expect(screen.getByLabelText('Nueva base de Queso · Por kg')).toHaveValue('950')
    // Not part of the batch: stays.
    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveValue('2500,55')
  })

  it('while a picked date loads its prices, the remark and publishing wait and the old prices are not shown', async () => {
    let release!: () => void
    futureBreakdownHandler = () =>
      new Promise<Response>((resolve) => {
        release = () => resolve(json({ ...breakdownOf(minorista, futureItems), on: '2026-10-20' }))
      })
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1200')
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeEnabled()
    await user.type(screen.getByLabelText('Vigente desde'), '2026-10-20')

    expect(await screen.findByText('Cargando los precios vigentes en la fecha elegida…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeDisabled()
    // Today's prices are not the baseline of 2026-10-20.
    expect(within(rowOf('Lengua')).queryByText('$ 1.000,00')).not.toBeInTheDocument()
    expect(within(rowOf('Lengua')).queryByText('$ 1.210,00')).not.toBeInTheDocument()
    expect(within(rowOf('Lengua')).queryByText(/\$ 1\.000,00 →/)).not.toBeInTheDocument()

    release()

    expect(await within(rowOf('Lengua')).findByText('$ 1.100,00')).toBeInTheDocument()
    expect(within(rowOf('Lengua')).getByText('$ 1.100,00 → $ 1.200,00')).toBeInTheDocument()
    expect(screen.queryByText('Cargando los precios vigentes en la fecha elegida…')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeEnabled()
  })

  it('a failed load of the picked date blocks publishing until "Reintentar" loads it', async () => {
    futureBreakdownHandler = () => json({ title: 'No se pudo leer la lista.' }, 500)
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1200')
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.type(screen.getByLabelText('Vigente desde'), '2026-10-20')

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('No se pudo leer la lista.')
    expect(screen.queryByText('Cargando los precios vigentes en la fecha elegida…')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeDisabled()
    // The previous date's prices are not presented as the current ones.
    expect(within(rowOf('Lengua')).queryByText('$ 1.000,00')).not.toBeInTheDocument()
    expect(within(rowOf('Lengua')).queryByText(/\$ 1\.000,00 →/)).not.toBeInTheDocument()

    futureBreakdownHandler = undefined
    const callsBefore = breakdownCalls(minorista.id).length
    await user.click(within(alert).getByRole('button', { name: 'Reintentar' }))

    expect(await within(rowOf('Lengua')).findByText('$ 1.100,00')).toBeInTheDocument()
    expect(breakdownCalls(minorista.id)).toHaveLength(callsBefore + 1)
    expect(breakdownCalls(minorista.id).at(-1)?.[0]).toBe(`/pricing/price-lists/${minorista.id}/breakdown?on=2026-10-20`)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Aplicar' })).toBeEnabled()
  })

  it('registers its pending edits with the app-wide guard only while there are any', async () => {
    const user = await openEditor({ withAppGuard: true })

    expect(screen.getByTestId('app-guard')).toHaveTextContent('')
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    expect(screen.getByTestId('app-guard')).toHaveTextContent('Tenés 1 cambio sin publicar.')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), '2600')
    expect(screen.getByTestId('app-guard')).toHaveTextContent('Tenés 2 cambios sin publicar.')

    await user.click(screen.getByRole('button', { name: 'Deshacer cambios' }))
    expect(screen.getByTestId('app-guard')).toHaveTextContent('')
  })

  it('drops its registration with the app-wide guard when the editor closes', async () => {
    const user = await openEditor({ withAppGuard: true })

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    expect(screen.getByTestId('app-guard')).toHaveTextContent('Tenés 1 cambio sin publicar.')
    await user.click(screen.getByRole('button', { name: 'Precios' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Descartar' }))

    expect(screen.queryByTestId('price-editor-grid')).not.toBeInTheDocument()
    expect(screen.getByTestId('app-guard')).toHaveTextContent('')
  })

  it('an invalid value on a filtered-out row is named in the summary, and "Mostrar" brings it back', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), 'abc')
    await user.type(screen.getByLabelText('Buscar productos'), 'queso')
    await user.selectOptions(screen.getByLabelText('Categoría'), 'cat-lacteos')
    expect(screen.queryByLabelText('Nueva base de Vacío')).not.toBeInTheDocument()

    expect(screen.getByText('1 fila con valores inválidos')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Mostrar' }))

    expect(screen.getByLabelText('Buscar productos')).toHaveValue('')
    expect(screen.getByLabelText('Categoría')).toHaveValue('')
    await waitFor(() => expect(screen.getByLabelText('Nueva base de Vacío')).toHaveFocus())
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()
  })

  it('counts every invalid row in the summary', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Nueva base de Lengua'), '0')
    await user.type(screen.getByLabelText('Nueva base de Vacío'), 'abc')

    expect(screen.getByText('2 filas con valores inválidos')).toBeInTheDocument()
    await user.clear(screen.getByLabelText('Nueva base de Lengua'))
    await user.clear(screen.getByLabelText('Nueva base de Vacío'))
    expect(screen.queryByText(/con valores inválidos/)).not.toBeInTheDocument()
  })

  it('a picked date loads the prices of that date as the baseline for changes and the remark', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Vigente desde'), '2026-10-20')

    await waitFor(() =>
      expect(breakdownCalls(minorista.id).map(([url]) => url)).toContain(
        `/pricing/price-lists/${minorista.id}/breakdown?on=2026-10-20`,
      ),
    )
    expect(await within(rowOf('Lengua')).findByText('$ 1.100,00')).toBeInTheDocument()
    expect(within(rowOf('Lengua')).getByText('$ 1.331,00')).toBeInTheDocument()

    // 1100 is already the price of that date: not a change.
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    expect(rowOf('Lengua')).not.toHaveAttribute('data-changed')
    expect(screen.getByRole('button', { name: 'Publicar cambios' })).toBeDisabled()

    await user.clear(screen.getByLabelText('Nueva base de Lengua'))
    await user.click(screen.getByLabelText('Seleccionar Lengua'))
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))
    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('1210,00')

    // Back to no date: today's prices again.
    await user.clear(screen.getByLabelText('Vigente desde'))
    expect(await within(rowOf('Lengua')).findByText('$ 1.000,00')).toBeInTheDocument()
  })

  it('the date cannot be earlier than the business day', async () => {
    await openEditor()

    // The breakdown without a date is the server's business day.
    await waitFor(() => expect(screen.getByLabelText('Vigente desde')).toHaveAttribute('min', '2026-10-04'))
  })

  it('a refused effective date is shown on the date field', async () => {
    batchHandler = () =>
      json(
        {
          title: 'One or more validation errors occurred.',
          errors: { effectiveFrom: ['effectiveFrom cannot be earlier than today.'] },
        },
        400,
      )
    const user = await openEditor()

    await user.type(screen.getByLabelText('Vigente desde'), '2026-10-10')
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    await user.click(screen.getByRole('button', { name: 'Publicar cambios' }))

    const date = screen.getByLabelText('Vigente desde')
    await waitFor(() => expect(date).toHaveAttribute('aria-invalid', 'true'))
    expect(date).toHaveAccessibleDescription(/effectiveFrom cannot be earlier than today\./)

    await user.clear(date)
    expect(date).not.toHaveAttribute('aria-invalid')
  })

  it('warns before closing or reloading the browser tab only while edits are pending', async () => {
    const user = await openEditor()
    const unload = () => {
      const event = new Event('beforeunload', { cancelable: true })
      window.dispatchEvent(event)
      return event.defaultPrevented
    }

    expect(unload()).toBe(false)
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')
    expect(unload()).toBe(true)
    await user.click(screen.getByRole('button', { name: 'Deshacer cambios' }))
    expect(unload()).toBe(false)
  })

  it('shows the composition and simulates the new final of a new base', async () => {
    const user = await openEditor()

    expect(await screen.findByText('IVA 21 % sobre base')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Nueva base de Lengua'), '1100')

    const row = rowOf('Lengua')
    expect(within(row).getByText('$ 1.000,00 → $ 1.100,00')).toBeInTheDocument()
    expect(within(row).getByText(/Final: .*1\.210,00 → .*1\.331,00/)).toBeInTheDocument()
  })

  it('filters the products without a price in the list, to give them one', async () => {
    const user = await openEditor()

    await user.selectOptions(screen.getByLabelText('Precio en la lista'), 'missing')

    expect(screen.getByRole('option', { name: 'Sin precio (1)' })).toBeInTheDocument()
    expect(rowOf('Horma')).toBeInTheDocument()
    expect(within(screen.getByTestId('price-editor-grid')).queryByText('Lengua')).not.toBeInTheDocument()
  })

  it('remarks only the rows the filter shows', async () => {
    const user = await openEditor()

    await user.type(screen.getByLabelText('Buscar productos'), 'Lengua')
    expect(screen.getByText('Se aplica a 1 producto: el que muestra el filtro.')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Remarcar %'), '10')
    await user.click(screen.getByRole('button', { name: 'Aplicar' }))
    await user.clear(screen.getByLabelText('Buscar productos'))

    expect(screen.getByLabelText('Nueva base de Lengua')).toHaveValue('1100,00')
    expect(screen.getByLabelText('Nueva base de Vacío')).toHaveValue('')
  })
})
