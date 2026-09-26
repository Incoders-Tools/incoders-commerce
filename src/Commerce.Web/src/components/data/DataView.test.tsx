import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { DataView, type DataViewColumn } from './DataView'

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
})
