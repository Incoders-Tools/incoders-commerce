import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ImportReviewTable, type ImportReviewRow } from './ImportReviewTable'

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`": one
 * row per `price_import_rows` row (row #, raw code, matched item, current ->
 * proposed, status badge) and Commit disabled when no row is `Matched`. This
 * is a structural shell — Work Unit 9 wires it to the real upload/review
 * endpoints, which do not exist yet.
 */
describe('ImportReviewTable', () => {
  const unmatchedRows: ImportReviewRow[] = [
    { rowNumber: 1, rawCode: '7791234567890', matchedItemName: null, currentPrice: null, proposedPrice: 10, status: 'UnknownCode' },
    { rowNumber: 2, rawCode: '7791234567891', matchedItemName: 'Coca Cola 1.5L', currentPrice: 500, proposedPrice: 500, status: 'NoChange' },
  ]

  it('renders one row per review row with its status badge', () => {
    render(<ImportReviewTable rows={unmatchedRows} onCommit={vi.fn()} onReject={vi.fn()} />)

    expect(screen.getByTestId('import-row-status-1')).toHaveTextContent('UnknownCode')
    expect(screen.getByTestId('import-row-status-2')).toHaveTextContent('NoChange')
    expect(screen.getByText('Coca Cola 1.5L')).toBeInTheDocument()
  })

  it('disables Commit when zero rows are Matched', () => {
    render(<ImportReviewTable rows={unmatchedRows} onCommit={vi.fn()} onReject={vi.fn()} />)

    expect(screen.getByRole('button', { name: /commit/i })).toBeDisabled()
  })

  it('enables Commit when at least one row is Matched, and invokes onCommit', async () => {
    const rows: ImportReviewRow[] = [
      ...unmatchedRows,
      { rowNumber: 3, rawCode: '7791234567892', matchedItemName: 'Sprite 1.5L', currentPrice: 480, proposedPrice: 510, status: 'Matched' },
    ]
    const onCommit = vi.fn()
    const user = userEvent.setup()
    render(<ImportReviewTable rows={rows} onCommit={onCommit} onReject={vi.fn()} />)

    const commitButton = screen.getByRole('button', { name: /commit/i })
    expect(commitButton).toBeEnabled()
    await user.click(commitButton)

    expect(onCommit).toHaveBeenCalledTimes(1)
  })

  it('shows an empty state with zero rows and keeps Commit disabled', () => {
    render(<ImportReviewTable rows={[]} onCommit={vi.fn()} onReject={vi.fn()} />)

    expect(screen.getByText('No rows to review yet.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /commit/i })).toBeDisabled()
  })
})
