import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import type { ReceptionRecord } from '@/api/types'
import { ReceptionScreen } from './ReceptionScreen'
import { json, supplierFixture } from './supplierFixtures'
import { SUPPLIER_ID, catalogPresentation, catalogProduct, receptionRecord } from './receptionFixtures'

describe('ReceptionScreen', () => {
  const fetchMock = vi.fn()
  let stored: ReceptionRecord

  const callsTo = (method: string, matcher: (url: string) => boolean) =>
    fetchMock.mock.calls.filter(([url, init]) => (init?.method ?? 'GET') === method && matcher(url as string))

  const draft = (overrides: Partial<ReceptionRecord> = {}) =>
    receptionRecord({
      status: 'Draft',
      number: null,
      dueOn: null,
      ledgerInvoiceMovementId: null,
      confirmedAtUtc: null,
      ...overrides,
    })

  beforeEach(() => {
    stored = receptionRecord()
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/suppliers')) return json([supplierFixture()])
      if (url === '/catalog/products') return json([catalogProduct('p-1', 'Media res')])
      if (url === '/catalog/presentations') {
        return json([catalogPresentation('pr-1', 'p-1', 'Kilo', 1), catalogPresentation('pr-2', 'p-1', 'Pieza', 0)])
      }
      if (url === '/purchases/receptions' && init?.method === 'POST') return json(draft({ id: 'rec-9' }), 201)
      if (url.startsWith('/purchases/receptions/') && url.endsWith('/confirm')) {
        return json(receptionRecord({ id: 'rec-9', number: 'R01-W-1', dueOn: '2026-10-31' }))
      }
      if (url.startsWith('/purchases/receptions/') && url.endsWith('/void') && init?.method === 'POST') {
        return json({ ...stored, status: 'Voided', voidReason: JSON.parse(init.body as string).reason })
      }
      if (url.startsWith('/purchases/receptions/') && init?.method === 'PUT') return json(draft(), 200)
      if (url.startsWith('/purchases/receptions/')) return json(stored)
      throw new Error(`unexpected ${url}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const renderAt = (path: string, branch: 'none' | 'selected' = 'selected') =>
    render(
      <MemoryRouter initialEntries={[path]}>
        <BranchContext.Provider
          value={{
            selectedBranch: branch === 'none' ? null : { id: 'b-1', name: 'Ruta 51', code: 1 },
            selectableBranches: [],
            selectBranch: () => {},
          }}
        >
          <Routes>
            <Route path="/app/receptions" element={<p>Lista de recepciones</p>} />
            <Route path="/app/receptions/new" element={<ReceptionScreen />} />
            <Route path="/app/receptions/:id" element={<ReceptionScreen />} />
          </Routes>
        </BranchContext.Provider>
      </MemoryRouter>,
    )

  const fillSupplier = async () => {
    await waitFor(() => expect(screen.getByRole('option', { name: 'Frigorífico Norte' })).toBeInTheDocument())
    fireEvent.change(screen.getByLabelText('Proveedor'), { target: { value: SUPPLIER_ID } })
  }

  const fillFirstLine = async (quantity: string, unitCost: string) => {
    const user = userEvent.setup()
    await user.click(screen.getByRole('combobox', { name: 'Presentación (línea 1)' }))
    await user.click(await screen.findByRole('option', { name: /Media res — Kilo/ }))
    fireEvent.change(screen.getByLabelText('Cantidad (línea 1)'), { target: { value: quantity } })
    fireEvent.change(screen.getByLabelText('Costo unitario (línea 1)'), { target: { value: unitCost } })
  }

  it('creates a draft of 120 kg at 4.000, totals 480.000, then confirms it and shows its number', async () => {
    const user = userEvent.setup()
    renderAt('/app/receptions/new')

    await fillSupplier()
    await fillFirstLine('120', '4000')
    expect(screen.getByTestId('reception-total')).toHaveTextContent(/480\.000,00/)
    expect(screen.getByLabelText('Cantidad (línea 1)')).toBeInTheDocument()
    expect(screen.getByText('kg')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Confirmar recepción' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/mueve el stock/i)
    expect(dialog).toHaveTextContent(/cuenta corriente del proveedor/i)
    await user.click(within(dialog).getByRole('button', { name: 'Confirmar y mover stock' }))

    expect(await screen.findByText('R01-W-1')).toBeInTheDocument()
    expect(screen.getByText('Confirmada')).toBeInTheDocument()
    const [, init] = callsTo('POST', (url) => url === '/purchases/receptions')[0]
    expect(JSON.parse(init.body as string)).toMatchObject({
      supplierId: SUPPLIER_ID,
      documentType: 'Invoice',
      lines: [{ presentationId: 'pr-1', quantity: 120, unitCost: 4000 }],
    })
    expect(callsTo('POST', (url) => url === '/purchases/receptions/rec-9/confirm')).toHaveLength(1)
  })

  it('saves a draft and opens it for editing', async () => {
    const user = userEvent.setup()
    renderAt('/app/receptions/new')

    await fillSupplier()
    await fillFirstLine('120', '4000')
    await user.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('Borrador guardado.')).toBeInTheDocument()
    expect(screen.getByText('Borrador')).toBeInTheDocument()
  })

  it('shows the field errors the server returns next to the line they belong to', async () => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/suppliers')) return json([supplierFixture()])
      if (url === '/catalog/products') return json([catalogProduct('p-1', 'Media res')])
      if (url === '/catalog/presentations') return json([catalogPresentation('pr-1', 'p-1', 'Kilo', 1)])
      if (init?.method === 'POST') {
        return json({ title: 'Validation', errors: { 'lines[0].quantity': ['La cantidad debe ser mayor a cero.'] } }, 400)
      }
      throw new Error(`unexpected ${url}`)
    })
    const user = userEvent.setup()
    renderAt('/app/receptions/new')

    await fillSupplier()
    await fillFirstLine('120', '4000')
    await user.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('La cantidad debe ser mayor a cero.')).toBeInTheDocument()
  })

  it('does not call the API without a supplier and a presented line', async () => {
    const user = userEvent.setup()
    renderAt('/app/receptions/new')
    await waitFor(() => expect(screen.getByRole('option', { name: 'Frigorífico Norte' })).toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: 'Guardar borrador' }))

    expect(await screen.findByText('Elegí un proveedor.')).toBeInTheDocument()
    expect(screen.getByText('Elegí una presentación.')).toBeInTheDocument()
    expect(callsTo('POST', (url) => url === '/purchases/receptions')).toHaveLength(0)
  })

  it('adds and removes lines and keeps the total live', async () => {
    const user = userEvent.setup()
    renderAt('/app/receptions/new')
    await fillSupplier()
    await fillFirstLine('2,5', '1000')

    await user.click(screen.getByRole('button', { name: 'Agregar línea' }))
    expect(screen.getByLabelText('Cantidad (línea 2)')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Cantidad (línea 2)'), { target: { value: '4' } })
    fireEvent.change(screen.getByLabelText('Costo unitario (línea 2)'), { target: { value: '500' } })
    expect(screen.getByTestId('reception-total')).toHaveTextContent(/4\.500,00/)

    await user.click(screen.getByRole('button', { name: 'Quitar línea 2' }))
    expect(screen.queryByLabelText('Cantidad (línea 2)')).not.toBeInTheDocument()
    expect(screen.getByTestId('reception-total')).toHaveTextContent(/2\.500,00/)
  })

  it('names the duplicate supplier document when confirming is refused', async () => {
    stored = draft({ id: 'rec-1' })
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/suppliers')) return json([supplierFixture()])
      if (url === '/catalog/products') return json([catalogProduct('p-1', 'Media res')])
      if (url === '/catalog/presentations') return json([catalogPresentation('pr-1', 'p-1', 'Kilo', 1)])
      if (url.endsWith('/confirm')) return json({ title: 'dup', error: 'reception-duplicate-document' }, 409)
      if (init?.method === 'PUT') return json(stored)
      return json(stored)
    })
    const user = userEvent.setup()
    renderAt('/app/receptions/rec-1')

    await user.click(await screen.findByRole('button', { name: 'Confirmar recepción' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Confirmar y mover stock' }))

    expect(await screen.findByText(/ya hay una recepción confirmada con ese documento/i)).toBeInTheDocument()
  })

  it('offers to reload when the draft was changed by someone else', async () => {
    stored = draft({ id: 'rec-1' })
    let putAttempts = 0
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/suppliers')) return json([supplierFixture()])
      if (url === '/catalog/products') return json([catalogProduct('p-1', 'Media res')])
      if (url === '/catalog/presentations') return json([catalogPresentation('pr-1', 'p-1', 'Kilo', 1)])
      if (init?.method === 'PUT') {
        putAttempts += 1
        return json({ title: 'modified', error: 'reception-modified' }, 409)
      }
      return json(stored)
    })
    const user = userEvent.setup()
    renderAt('/app/receptions/rec-1')

    await user.click(await screen.findByRole('button', { name: 'Guardar borrador' }))
    expect(await screen.findByText(/otra persona modificó esta recepción/i)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Recargar' }))

    await waitFor(() => expect(screen.queryByText(/otra persona modificó/i)).not.toBeInTheDocument())
    expect(putAttempts).toBe(1)
  })

  it('opens a confirmed reception read only with its number, supplier account link and void action', async () => {
    renderAt('/app/receptions/rec-1')

    expect(await screen.findByText('R01-W-1')).toBeInTheDocument()
    expect(screen.getByText('Confirmada')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar borrador' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Confirmar recepción' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Ver cuenta corriente del proveedor' })).toHaveAttribute(
      'href',
      `/app/suppliers/${SUPPLIER_ID}/account`,
    )
    expect(screen.getByText('Media res')).toBeInTheDocument()
    expect(screen.getByText('120 kg')).toBeInTheDocument()
    expect(screen.getByText('L-77')).toBeInTheDocument()
  })

  it('voids a confirmed reception only with a reason and shows it as voided', async () => {
    const user = userEvent.setup()
    renderAt('/app/receptions/rec-1')

    await user.click(await screen.findByRole('button', { name: 'Anular recepción' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent(/revierte el stock/i)
    expect(dialog).toHaveTextContent(/factura/i)

    await user.click(within(dialog).getByRole('button', { name: 'Anular y revertir' }))
    expect(within(dialog).getByText('Escribí el motivo de la anulación.')).toBeInTheDocument()
    expect(callsTo('POST', (url) => url.endsWith('/void'))).toHaveLength(0)

    fireEvent.change(within(dialog).getByLabelText('Motivo de la anulación'), { target: { value: 'Mercadería rechazada' } })
    await user.click(within(dialog).getByRole('button', { name: 'Anular y revertir' }))

    expect(await screen.findByText('Anulada')).toBeInTheDocument()
    expect(screen.getByText(/Mercadería rechazada/)).toBeInTheDocument()
    const [, init] = callsTo('POST', (url) => url.endsWith('/void'))[0]
    expect(JSON.parse(init.body as string)).toEqual({ reason: 'Mercadería rechazada' })
    expect(screen.queryByRole('button', { name: 'Anular recepción' })).not.toBeInTheDocument()
  })

  it('tells the operator to pick a branch', async () => {
    renderAt('/app/receptions/new', 'none')

    expect(await screen.findByText(/Elegí una sucursal/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Guardar borrador' })).not.toBeInTheDocument()
  })

  it('shows a not found message for a reception outside the branch', async () => {
    fetchMock.mockImplementation(async (url: string) =>
      url.startsWith('/purchases/receptions/') ? json({ title: 'nf' }, 404) : json([]),
    )
    renderAt('/app/receptions/zzz')

    expect(await screen.findByText(/no existe o pertenece a otra sucursal/i)).toBeInTheDocument()
  })
})
