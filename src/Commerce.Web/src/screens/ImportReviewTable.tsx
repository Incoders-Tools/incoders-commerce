import { useTranslation } from 'react-i18next'
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
const STATUS_KEYS: Record<ImportRowStatus, 'matched' | 'noChange' | 'unknownCode' | 'invalidPrice' | 'duplicateInFile'> = {
  Matched: 'matched',
  NoChange: 'noChange',
  UnknownCode: 'unknownCode',
  InvalidPrice: 'invalidPrice',
  DuplicateInFile: 'duplicateInFile',
}

export function ImportReviewTable({ rows, onCommit, onReject, committing = false }: ImportReviewTableProps) {
  const { t } = useTranslation('priceLists')
  const hasMatchedRow = rows.some((row) => row.status === 'Matched')

  return (
    <div className="flex flex-col gap-3">
      {rows.length === 0 ? (
        <p>{t('import.reviewTable.empty')}</p>
      ) : (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left">
              <th>{t('import.reviewTable.columns.row')}</th>
              <th>{t('import.reviewTable.columns.code')}</th>
              <th>{t('import.reviewTable.columns.item')}</th>
              <th>{t('import.reviewTable.columns.current')}</th>
              <th>{t('import.reviewTable.columns.proposed')}</th>
              <th>{t('import.reviewTable.columns.status')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.rowNumber} className="border-b border-border">
                <td>{row.rowNumber}</td>
                <td>{row.rawCode}</td>
                <td>{row.matchedItemName ?? '—'}</td>
                <td>{row.currentPrice !== null ? row.currentPrice.toFixed(2) : '—'}</td>
                <td>{row.proposedPrice.toFixed(2)}</td>
                <td>
                  <span data-testid={`import-row-status-${row.rowNumber}`}>
                    {t(`import.reviewTable.statusOptions.${STATUS_KEYS[row.status]}`)}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="flex gap-2">
        <Button onClick={onCommit} disabled={!hasMatchedRow || committing}>
          {committing ? t('import.reviewTable.committing') : t('import.reviewTable.commit')}
        </Button>
        <Button variant="outline" onClick={onReject} disabled={rows.length === 0 || committing}>
          {t('import.reviewTable.reject')}
        </Button>
      </div>
    </div>
  )
}
