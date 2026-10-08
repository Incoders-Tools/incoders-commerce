import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { presentationLabel, type PresentationOption } from '@/components/purchasing/presentationOptions'
import { listStock, setStockMinimum } from '@/api/stock'
import { ApiError } from '@/api/client'
import type { StockLevel } from '@/api/types'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatIsoDate } from '@/dashboard/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { useNumberFormat } from '@/organization/NumberFormatContext'
import { cn } from '@/lib/utils'
import { StockAdjustForm } from './StockAdjustForm'

const SEARCH_DEBOUNCE_MS = 300

type StockStatus = 'negative' | 'out' | 'low' | 'ok'

/** Negative and empty stock outrank "below minimum": they are the more urgent thing to say. */
export function stockStatus(row: StockLevel): StockStatus {
  if (row.onHand < 0) return 'negative'
  if (row.onHand === 0) return 'out'
  return row.belowMinimum ? 'low' : 'ok'
}

const badgeStyles: Record<StockStatus, string> = {
  negative: 'border-destructive/40 bg-destructive/10 text-destructive',
  out: 'border-destructive/40 bg-destructive/10 text-destructive',
  low: 'border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-400',
  ok: 'border-border bg-muted text-muted-foreground',
}

const optionOf = (row: StockLevel): PresentationOption => ({
  id: row.presentationId,
  productName: row.productName,
  presentationName: row.presentationName,
  behavior: row.quantityBehavior,
  identificationCode: row.identificationCode,
})

interface MinimumEdit {
  presentationId: string
  value: string
  error: string | null
  saving: boolean
}

/**
 * On-hand stock of the selected branch (`GET /stock`): search and "only below
 * minimum" are applied by the server. A row can edit its minimum inline, open
 * the adjustment form for its presentation or jump to its movement history.
 */
export function StockScreen() {
  const { t } = useTranslation('stock')
  const numberFormat = useNumberFormat()
  const missingBranch = useMissingBranch()
  const [rows, setRows] = useState<StockLevel[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [onlyBelowMinimum, setOnlyBelowMinimum] = useState(false)
  const [view, setView] = useViewPreference('stock')
  const [editing, setEditing] = useState<MinimumEdit | null>(null)
  const [adjusting, setAdjusting] = useState<PresentationOption | 'new' | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    if (missingBranch) return
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listStock({ search: debouncedSearch, onlyBelowMinimum })
      if (request === latestRequest.current) setRows(result)
    } catch (err) {
      if (request === latestRequest.current) {
        setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
      }
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, missingBranch, debouncedSearch, onlyBelowMinimum])

  useEffect(() => {
    void refresh()
  }, [refresh])

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('title')} description={t('description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  if (adjusting !== null) {
    return (
      <StockAdjustForm
        presentation={adjusting === 'new' ? null : adjusting}
        onCancel={() => setAdjusting(null)}
        onDone={(message) => {
          setAdjusting(null)
          setNotice(message)
          void refresh()
        }}
      />
    )
  }

  const saveMinimum = async () => {
    if (!editing) return
    const text = editing.value.trim()
    const minimum = text === '' ? null : numberFormat.parse(text)
    if (text !== '' && (minimum === null || minimum < 0)) {
      setEditing({ ...editing, error: numberFormat.errorFor(text) ?? t('minimum.invalid') })
      return
    }
    setEditing({ ...editing, saving: true, error: null })
    try {
      await setStockMinimum(editing.presentationId, minimum)
      setEditing(null)
      void refresh()
    } catch (err) {
      setEditing({
        ...editing,
        saving: false,
        error: err instanceof ApiError ? err.message : t('errors.unexpectedSave'),
      })
    }
  }

  const noValue = <span className="text-muted-foreground">{t('columns.noValue')}</span>
  const columns: DataViewColumn<StockLevel>[] = [
    { key: 'product', header: t('columns.product'), cell: (row) => row.productName },
    {
      key: 'presentation',
      header: t('columns.presentation'),
      cell: (row) => (
        <span className="flex flex-col">
          <span>{row.presentationName}</span>
          {row.identificationCode && <span className="text-xs text-muted-foreground">{row.identificationCode}</span>}
        </span>
      ),
    },
    {
      key: 'onHand',
      header: t('columns.onHand'),
      cell: (row) => (
        <span className={cn('tabular-nums', row.onHand < 0 && 'font-semibold text-destructive')}>
          {numberFormat.formatStock(row.onHand, row.quantityBehavior)}
        </span>
      ),
    },
    {
      key: 'minimum',
      header: t('columns.minimum'),
      cell: (row) =>
        row.minimumQuantity === null ? noValue : numberFormat.formatStock(row.minimumQuantity, row.quantityBehavior),
      hideOnMobile: true,
    },
    {
      key: 'status',
      header: t('columns.status'),
      cell: (row) => {
        const status = stockStatus(row)
        return (
          <span className={cn('inline-flex rounded-full border px-2 py-0.5 text-xs font-medium', badgeStyles[status])}>
            {t(`status.${status}`)}
          </span>
        )
      },
    },
    {
      key: 'lastMovement',
      header: t('columns.lastMovement'),
      cell: (row) => (row.lastMovementAtUtc ? formatIsoDate(row.lastMovementAtUtc) : noValue),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={<Button onClick={() => setAdjusting('new')}>{t('adjust')}</Button>}
      />

      {notice && (
        <p role="status" className="rounded-md border border-emerald-500/40 bg-emerald-500/10 px-4 py-3 text-sm text-emerald-700 dark:text-emerald-400">
          {notice}
        </p>
      )}
      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel={t('search.label')}
        searchPlaceholder={t('search.placeholder')}
        view={view}
        onViewChange={setView}
      >
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={onlyBelowMinimum}
            onChange={(event) => setOnlyBelowMinimum(event.target.checked)}
          />
          {t('filters.onlyBelowMinimum')}
        </label>
      </DataToolbar>

      <DataView
        items={rows}
        columns={columns}
        getRowKey={(row) => row.presentationId}
        view={view}
        loading={loading}
        emptyMessage={debouncedSearch !== '' || onlyBelowMinimum ? t('empty.noMatch') : t('empty.none')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(row) => {
          if (editing?.presentationId === row.presentationId) {
            const name = presentationLabel(row)
            return (
              <div className="flex flex-col items-end gap-1">
                <div className="flex flex-wrap items-center justify-end gap-2">
                  <Input
                    aria-label={t('minimum.label', { name })}
                    aria-invalid={editing.error ? true : undefined}
                    className="h-8 w-28"
                    inputMode="decimal"
                    placeholder={numberFormat.example}
                    value={editing.value}
                    onChange={(event) => setEditing({ ...editing, value: event.target.value, error: null })}
                  />
                  <Button size="sm" disabled={editing.saving} onClick={() => void saveMinimum()}>
                    {t('minimum.save')}
                  </Button>
                  <Button variant="outline" size="sm" disabled={editing.saving} onClick={() => setEditing(null)}>
                    {t('minimum.cancel')}
                  </Button>
                </div>
                <p className="text-xs text-muted-foreground">{t('minimum.hint')}</p>
                {editing.error && <p className="text-xs text-destructive">{editing.error}</p>}
              </div>
            )
          }
          return (
            <>
              <Link
                to={`/app/stock/${row.presentationId}/movements`}
                state={{ label: presentationLabel(row), behavior: row.quantityBehavior }}
                className={buttonVariants({ variant: 'outline', size: 'sm' })}
              >
                {t('actions.movements')}
              </Link>
              <Button
                variant="outline"
                size="sm"
                onClick={() =>
                  setEditing({
                    presentationId: row.presentationId,
                    value: row.minimumQuantity === null ? '' : String(row.minimumQuantity).replace('.', ','),
                    error: null,
                    saving: false,
                  })
                }
              >
                {t('actions.minimum')}
              </Button>
              <Button variant="outline" size="sm" onClick={() => setAdjusting(optionOf(row))}>
                {t('actions.adjust')}
              </Button>
            </>
          )
        }}
      />
    </section>
  )
}
