import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchContext } from '@/branch/BranchContext'
import { ReceptionsScreen } from './ReceptionsScreen'
import { json, supplierFixture } from './supplierFixtures'
import { receptionSummary } from './receptionFixtures'

describe('ReceptionsScreen', () => {
  const fetchMock = vi.fn()

  const receptionCalls = () =>
    fetchMock.mock.calls.map((call) => call[0] as string).filter((url) => url.startsWith('/purchases/receptions'))

  beforeEach(() => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url.startsWith('/suppliers')) return json([supplierFixture()])
      return json([
        receptionSummary(),
        receptionSummary({ id: 'rec-2', status: 'Draft', number: null, documentReference: null, totalAmount: 1500 }),
      ])
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const renderScreen = () =>
    render(
      <MemoryRouter>
        <ReceptionsScreen />
      </MemoryRouter>,
    )

  it('lists receptions with number, supplier, document, dates, total and status', async () => {
    renderScreen()

    const rows = within(await screen.findByRole('table')).getAllByRole('row')
    const confirmed = rows[1]
    expect(within(confirmed).getByText('R01-W-1')).toBeInTheDocument()
    expect(within(confirmed).getByText('Frigorífico Norte')).toBeInTheDocument()
    expect(within(confirmed).getByText('A-0001-00001234')).toBeInTheDocument()
    expect(within(confirmed).getByText('01/10/2026')).toBeInTheDocument()
    expect(within(confirmed).getByText('31/10/2026')).toBeInTheDocument()
    expect(within(confirmed).getByText(/480\.000,00/)).toBeInTheDocument()
    expect(within(confirmed).getByText('Confirmada')).toBeInTheDocument()
    expect(within(rows[2]).getByText('Borrador')).toBeInTheDocument()
    expect(within(rows[1]).getByRole('link', { name: 'Abrir' })).toHaveAttribute('href', '/app/receptions/rec-1')
  })

  it('links to a new reception', async () => {
    renderScreen()

    expect(await screen.findByRole('link', { name: 'Nueva recepción' })).toHaveAttribute('href', '/app/receptions/new')
  })

  it('asks the server for the chosen status, supplier, dates and search', async () => {
    renderScreen()
    await screen.findByRole('table')
    await waitFor(() => expect(screen.getByRole('option', { name: 'Frigorífico Norte' })).toBeInTheDocument())

    fireEvent.change(screen.getByLabelText('Estado'), { target: { value: 'Confirmed' } })
    fireEvent.change(screen.getByLabelText('Proveedor'), { target: { value: supplierFixture().id } })
    fireEvent.change(screen.getByLabelText('Desde'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Hasta'), { target: { value: '2026-10-31' } })
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'A-0001' } })

    await waitFor(() => {
      const last = receptionCalls().at(-1) ?? ''
      expect(last).toContain('status=Confirmed')
      expect(last).toContain(`supplierId=${supplierFixture().id}`)
      expect(last).toContain('from=2026-10-01')
      expect(last).toContain('to=2026-10-31')
      expect(last).toContain('search=A-0001')
    })
  })

  it('tells the operator to pick a branch instead of calling the API', async () => {
    render(
      <MemoryRouter>
        <BranchContext.Provider value={{ selectedBranch: null, selectableBranches: [], selectBranch: () => {} }}>
          <ReceptionsScreen />
        </BranchContext.Provider>
      </MemoryRouter>,
    )

    expect(await screen.findByText(/Elegí una sucursal/)).toBeInTheDocument()
    expect(receptionCalls()).toHaveLength(0)
  })

  it('shows the load error when the list cannot be read', async () => {
    fetchMock.mockImplementation(async (url: string) =>
      url.startsWith('/suppliers') ? json([]) : json({ title: 'boom' }, 500),
    )
    renderScreen()

    expect(await screen.findByRole('alert')).toHaveTextContent('boom')
  })
})
