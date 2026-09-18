import { Button } from '@/components/ui/button'

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`":
 * `match_status` per row from `price_import_rows`. This type is a
 * client-side placeholder for the row shape Work Unit 9's upload/review
 * endpoints will return — those endpoints and `price_import_rows` do not
 * exist yet (design.md "Import state machine"), so this component is a
 * structural shell driven entirely by props, not by a real fetch.
 */
export type ImportRowStatus = 'Matched' | 'NoChange' | 'UnknownCode' | 'InvalidPrice' | 'DuplicateInFile'

export interface ImportReviewRow {
  rowNumber: number
  rawCode: string
  matchedItemName: string | null
  currentPrice: number | null
  proposedPrice: number
  status: ImportRowStatus
}

interface ImportReviewTableProps {
  rows: ImportReviewRow[]
  onCommit: () => void
  onReject: () => void
  committing?: boolean
}

/**
 * Commit is disabled with zero `Matched` rows (design.md: "Commit is
 * disabled when no row is `Matched`") — a batch that only surfaces
 * unknown/duplicate/unchanged rows has nothing safe to publish.
 */
export function ImportReviewTable({ rows, onCommit, onReject, committing = false }: ImportReviewTableProps) {
  const hasMatchedRow = rows.some((row) => row.status === 'Matched')

  return (
    <div className="flex flex-col gap-3">
      {rows.length === 0 ? (
        <p>No rows to review yet.</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left">
              <th>#</th>
              <th>Code</th>
              <th>Item</th>
              <th>Current</th>
              <th>Proposed</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.rowNumber} className="border-b border-neutral-200">
                <td>{row.rowNumber}</td>
                <td>{row.rawCode}</td>
                <td>{row.matchedItemName ?? '—'}</td>
                <td>{row.currentPrice !== null ? row.currentPrice.toFixed(2) : '—'}</td>
                <td>{row.proposedPrice.toFixed(2)}</td>
                <td>
                  <span data-testid={`import-row-status-${row.rowNumber}`}>{row.status}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="flex gap-2">
        <Button onClick={onCommit} disabled={!hasMatchedRow || committing}>
          {committing ? 'Committing…' : 'Commit'}
        </Button>
        <Button variant="outline" onClick={onReject} disabled={rows.length === 0 || committing}>
          Reject
        </Button>
      </div>
    </div>
  )
}
