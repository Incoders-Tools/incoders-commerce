import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { DataView, nextSort, type DataViewColumn, type DataViewSort } from './DataView'

interface Row {
  id: string
  name: string
  code: string
}

const columns: DataViewColumn<Row>[] = [
  { key: 'name', header: 'Name', cell: (row) => row.name },
  { key: 'code', header: 'Code', cell: (row) => row.code },
]

const rows: Row[] = [
  { id: 'a', name: 'Alpha', code: 'A-1' },
  { id: 'b', name: 'Beta', code: 'B-2' },
]

function renderView(props: Partial<React.ComponentProps<typeof DataView<Row>>> = {}) {
  return render(
    <DataView
      items={rows}
      columns={columns}
      getRowKey={(row) => row.id}
      view="table"
      emptyMessage="No rows yet."
      {...props}
    />,
  )
}

/**
 * T4: shared data view. Handles the three states every data screen needs
 * (loading, empty, populated) in both the table and the card layout.
 */
describe('DataView', () => {
  it('renders a busy status while loading and no table', () => {
    renderView({ loading: true, items: [] })

    expect(screen.getByRole('status')).toHaveTextContent(/cargando/i)
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryByText('No rows yet.')).not.toBeInTheDocument()
  })

  it('renders a useful empty message instead of a blank area', () => {
    renderView({ items: [] })

    expect(screen.getByText('No rows yet.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('renders headers and one row per item in the table view', () => {
    renderView()

    expect(screen.getByRole('columnheader', { name: 'Name' })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Code' })).toBeInTheDocument()
    // header row + one row per item
    expect(screen.getAllByRole('row')).toHaveLength(3)
    expect(screen.getByText('Alpha')).toBeInTheDocument()
    expect(screen.getByText('B-2')).toBeInTheDocument()
  })

  it('renders a card per item, and no table, in the cards view', () => {
    renderView({ view: 'cards' })

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getByText('Alpha')).toBeInTheDocument()
    expect(screen.getByText('Beta')).toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
  })

  it('renders per-item actions in both views', () => {
    const { rerender } = renderView({ renderActions: (row) => <button type="button">Edit {row.name}</button> })

    expect(screen.getByRole('button', { name: 'Edit Alpha' })).toBeInTheDocument()

    rerender(
      <DataView
        items={rows}
        columns={columns}
        getRowKey={(row) => row.id}
        view="cards"
        emptyMessage="No rows yet."
        renderActions={(row) => <button type="button">Edit {row.name}</button>}
      />,
    )

    expect(screen.getByRole('button', { name: 'Edit Beta' })).toBeInTheDocument()
  })

  it('says the load failed instead of claiming the collection is empty', () => {
    renderView({ items: [], loadErrorMessage: 'Rows could not be loaded.' })

    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent('Rows could not be loaded.')
    // "there are none" and "I could not fetch them" are different answers.
    expect(screen.queryByText('No rows yet.')).not.toBeInTheDocument()
  })

  it('keeps showing the items it already has when a later load fails', () => {
    renderView({ loadErrorMessage: 'Rows could not be loaded.' })

    expect(screen.getAllByRole('row')).toHaveLength(3)
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
  })

  it('prefers the loading state over the load-failure message', () => {
    renderView({ items: [], loading: true, loadErrorMessage: 'Rows could not be loaded.' })

    expect(screen.getByRole('status')).toHaveTextContent(/cargando/i)
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
  })

  it('puts the optional row id on table rows and on cards, so a row can be linked to', () => {
    const { unmount } = renderView({ getRowId: (row) => `row-${row.id}` })
    expect(document.getElementById('row-a')?.tagName).toBe('TR')
    unmount()

    renderView({ view: 'cards', getRowId: (row) => `row-${row.id}` })
    expect(document.getElementById('row-b')?.tagName).toBe('ARTICLE')
  })
})

interface Level {
  id: string
  name: string
  quantity: number | null
  note: string
}

const levels: Level[] = [
  { id: '1', name: 'Vacío', quantity: 0, note: 'x' },
  { id: '2', name: 'asado', quantity: null, note: 'y' },
  { id: '3', name: 'Chorizo', quantity: -2.5, note: 'z' },
  { id: '4', name: 'Bife', quantity: 120, note: 'w' },
]

const sortableColumns: DataViewColumn<Level>[] = [
  { key: 'name', header: 'Name', cell: (row) => row.name, sortValue: (row) => row.name },
  { key: 'quantity', header: 'Quantity', cell: (row) => String(row.quantity ?? '-'), sortValue: (row) => row.quantity },
  { key: 'note', header: 'Note', cell: (row) => row.note },
]

function renderSorted(sort: DataViewSort | null, onSortChange = vi.fn()) {
  render(
    <DataView
      items={levels}
      columns={sortableColumns}
      getRowKey={(row) => row.id}
      view="table"
      emptyMessage="none"
      sort={sort}
      onSortChange={onSortChange}
    />,
  )
  return onSortChange
}

const namesInOrder = () =>
  screen
    .getAllByRole('row')
    .slice(1)
    .map((row) => within(row).getAllByRole('cell')[0].textContent)

/** owner-requests-quick-adjustments Q6: a list can opt into sorting by a column header. */
describe('DataView sorting', () => {
  it('turns only the headers of sortable columns into buttons and reports the clicked column', async () => {
    const onSortChange = renderSorted(null)

    expect(screen.queryByRole('button', { name: 'Note' })).not.toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Quantity' }))
    expect(onSortChange).toHaveBeenCalledWith({ key: 'quantity', direction: 'asc' })
    // Unsorted keeps the order the items came in.
    expect(namesInOrder()).toEqual(['Vacío', 'asado', 'Chorizo', 'Bife'])
  })

  it('orders numbers ascending and descending, always with the empty values last', () => {
    renderSorted({ key: 'quantity', direction: 'asc' })
    expect(namesInOrder()).toEqual(['Chorizo', 'Vacío', 'Bife', 'asado'])
    expect(screen.getByRole('columnheader', { name: 'Quantity' })).toHaveAttribute('aria-sort', 'ascending')
    expect(screen.getByRole('columnheader', { name: 'Name' })).toHaveAttribute('aria-sort', 'none')
  })

  it('puts the empty values last when descending too', () => {
    renderSorted({ key: 'quantity', direction: 'desc' })
    expect(namesInOrder()).toEqual(['Bife', 'Vacío', 'Chorizo', 'asado'])
    expect(screen.getByRole('columnheader', { name: 'Quantity' })).toHaveAttribute('aria-sort', 'descending')
  })

  it('orders text the Spanish way, ignoring case and accents', () => {
    renderSorted({ key: 'name', direction: 'asc' })
    expect(namesInOrder()).toEqual(['asado', 'Bife', 'Chorizo', 'Vacío'])
  })

  it('starts a new column ascending and flips the same column', () => {
    expect(nextSort(null, 'quantity')).toEqual({ key: 'quantity', direction: 'asc' })
    expect(nextSort({ key: 'quantity', direction: 'asc' }, 'quantity')).toEqual({ key: 'quantity', direction: 'desc' })
    expect(nextSort({ key: 'quantity', direction: 'desc' }, 'quantity')).toEqual({ key: 'quantity', direction: 'asc' })
    expect(nextSort({ key: 'quantity', direction: 'desc' }, 'name')).toEqual({ key: 'name', direction: 'asc' })
  })
})
