import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { listPrices } from '@/api/pricing'
import { ApiError } from '@/api/client'
import type { PresentationRecord, PriceListEntryRecord } from '@/api/types'

interface PriceDateFilterProps {
  priceListId: string
  presentations: PresentationRecord[]
}

function formatDate(value: string): string {
  const parsed = new Date(`${value}T00:00:00`)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString('es-AR')
}

/**
 * price-list-management spec "Price History Filterable By Date": a single
 * date (or a from/to range) shows the price each presentation had in
 * effect on that date, using the same effective-date resolution as
 * "Resolution By Effective Date" (`GetEffectiveAsync`/`ListAsOfAsync`/
 * `ListRangeAsync`, server-side). Clearing the filter returns to "now".
 * Read-only — filtering never writes or alters history.
 */
export function PriceDateFilter({ priceListId, presentations }: PriceDateFilterProps) {
  const { t } = useTranslation('priceLists')
  const [asOf, setAsOf] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [entries, setEntries] = useState<PriceListEntryRecord[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    async (filter: { asOf?: string } | { from: string; to: string } | Record<string, never>) => {
      setLoading(true)
      setError(null)
      try {
        const fetched = await listPrices(priceListId, filter)
        setEntries(fetched)
      } catch (err) {
        setError(err instanceof ApiError ? err.message : t('dateFilter.unexpectedLoad'))
      } finally {
        setLoading(false)
      }
    },
    [priceListId, t],
  )

  // "Now" on first mount and whenever the managed list changes — mirrors
  // the server default for no query params.
  useEffect(() => {
    void load({})
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [priceListId])

  const isRangeInput = from !== '' || to !== ''

  const handleApply = () => {
    if (isRangeInput) {
      if (from === '' || to === '') {
        setError(t('dateFilter.rangeBothRequired'))
        return
      }
      void load({ from, to })
      return
    }
    if (asOf !== '') {
      void load({ asOf })
      return
    }
    void load({})
  }

  const handleClear = () => {
    setAsOf('')
    setFrom('')
    setTo('')
    void load({})
  }

  const presentationName = (presentationId: string) =>
    presentations.find((p) => p.id === presentationId)?.name ?? presentationId

  return (
    <div className="flex w-full flex-col gap-3 rounded-md border border-border p-4">
      <p className="text-sm font-medium">{t('dateFilter.title')}</p>
      <div className="flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="date-filter-as-of">{t('dateFilter.asOfLabel')}</Label>
          <Input
            id="date-filter-as-of"
            type="date"
            value={asOf}
            onChange={(e) => {
              setAsOf(e.target.value)
              setFrom('')
              setTo('')
            }}
          />
        </div>
        <span className="text-sm text-muted-foreground">{t('dateFilter.or')}</span>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="date-filter-from">{t('dateFilter.fromLabel')}</Label>
          <Input
            id="date-filter-from"
            type="date"
            value={from}
            onChange={(e) => {
              setFrom(e.target.value)
              setAsOf('')
            }}
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="date-filter-to">{t('dateFilter.toLabel')}</Label>
          <Input
            id="date-filter-to"
            type="date"
            value={to}
            onChange={(e) => {
              setTo(e.target.value)
              setAsOf('')
            }}
          />
        </div>
        <Button type="button" size="sm" onClick={handleApply} disabled={loading}>
          {loading ? t('dateFilter.loading') : t('dateFilter.apply')}
        </Button>
        <Button type="button" variant="outline" size="sm" onClick={handleClear} disabled={loading}>
          {t('dateFilter.clear')}
        </Button>
      </div>

      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}

      {!loading && !error && entries.length === 0 && (
        <p className="text-sm text-muted-foreground">{t('dateFilter.empty')}</p>
      )}

      {entries.length > 0 && (
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-muted-foreground">
              <th className="pr-4 font-medium">{t('dateFilter.columns.presentation')}</th>
              <th className="pr-4 font-medium">{t('dateFilter.columns.price')}</th>
              <th className="font-medium">{t('dateFilter.columns.effectiveFrom')}</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((entry) => (
              <tr key={entry.id} className="border-t border-border">
                <td className="pr-4 py-1">{presentationName(entry.presentationId)}</td>
                <td className="pr-4 py-1">${entry.unitPrice.toFixed(2)}</td>
                <td className="py-1">{formatDate(entry.effectiveFrom)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
