import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { listHistory } from '@/api/pricing'
import { ApiError } from '@/api/client'
import type { PriceListEntryRecord } from '@/api/types'

interface PriceHistoryProps {
  priceListId: string
  presentationId: string
}

/**
 * Every published price of one presentation in one list, newest `effective_from` first. Fetches
 * when shown: the editor mounts it only inside the history side panel of the row the operator
 * asked about, so the grid never loads one history per row.
 */
export function PriceHistory({ priceListId, presentationId }: PriceHistoryProps) {
  const { t } = useTranslation('priceLists')
  const [entries, setEntries] = useState<PriceListEntryRecord[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    listHistory(priceListId, presentationId)
      .then((fetched) => {
        // Defensive sort — the store already orders by effective_from desc,
        // but the UI's contract ("newest first") should not depend on the
        // server never changing that.
        if (!cancelled) setEntries([...fetched].sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : -1)))
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof ApiError ? err.message : t('history.unexpectedLoad'))
      })
    return () => {
      cancelled = true
    }
  }, [priceListId, presentationId, t])

  return (
    <div className="text-sm">
      {entries === null && error === null && <p>{t('history.loading')}</p>}
      {error && (
        <p role="alert" className="text-destructive">
          {error}
        </p>
      )}
      {entries && entries.length === 0 && <p>{t('history.empty')}</p>}
      {entries && entries.length > 0 && (
        <ul className="flex flex-col gap-1">
          {entries.map((entry) => (
            <li key={entry.id}>
              {entry.effectiveFrom}: ${entry.unitPrice.toFixed(2)}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
