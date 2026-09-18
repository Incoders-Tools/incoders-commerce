import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { listHistory } from '@/api/pricing'
import { ApiError } from '@/api/client'
import type { PriceListEntryRecord } from '@/api/types'

interface PriceHistoryProps {
  priceListId: string
  presentationId: string
}

/**
 * Per-row History expander (design.md "Web: `PriceListsScreen` under the
 * existing `RequireAdmin`"): "listing every `effective_from` descending".
 * Fetches lazily on first expand only — the price list can hold many
 * presentations and eagerly loading every row's full history would be one
 * request per row on screen load.
 */
export function PriceHistory({ priceListId, presentationId }: PriceHistoryProps) {
  const [expanded, setExpanded] = useState(false)
  const [entries, setEntries] = useState<PriceListEntryRecord[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    if (!expanded && entries === null) {
      setLoading(true)
      setError(null)
      try {
        const fetched = await listHistory(priceListId, presentationId)
        // Defensive sort — the store already orders by effective_from desc,
        // but the UI's contract ("newest first") should not depend on the
        // server never changing that.
        setEntries([...fetched].sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : -1)))
      } catch (err) {
        setError(err instanceof ApiError ? err.message : 'Unexpected error loading price history.')
      } finally {
        setLoading(false)
      }
    }
    setExpanded((prev) => !prev)
  }

  return (
    <div>
      <Button type="button" variant="outline" size="sm" onClick={() => void toggle()}>
        {expanded ? 'Hide history' : 'History'}
      </Button>
      {expanded && (
        <div className="mt-2 text-sm">
          {loading && <p>Loading history…</p>}
          {error && (
            <p role="alert" className="text-red-600">
              {error}
            </p>
          )}
          {entries && entries.length === 0 && <p>No published prices yet.</p>}
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
      )}
    </div>
  )
}
