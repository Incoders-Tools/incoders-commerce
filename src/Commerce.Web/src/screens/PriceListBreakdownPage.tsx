import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { ViewSwitch, type DataViewMode } from '@/components/data/ViewSwitch'
import { useViewPreference } from '@/components/data/useViewPreference'
import { FormPage } from '@/components/layout/FormPage'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { ApiError } from '@/api/client'
import { floorViolationsOf, getBreakdown, getComposition, setFloor } from '@/api/pricing'
import type { BreakdownItem, CompositionRecord, FloorViolation, PriceListBreakdown, PriceListRecord } from '@/api/types'
import { FloorViolationsTable } from './FloorViolationsTable'
import { formatPercent, summarizeComponents } from './compositionSummary'

/** A phone gets the cards by default: the per-component columns do not fit its width. */
function phoneDefaultView(): DataViewMode {
  return typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 767px)').matches ? 'cards' : 'table'
}

interface PriceListBreakdownPageProps {
  priceList: PriceListRecord
  /** Every list of the branch: the floor can be any other one. */
  priceLists: PriceListRecord[]
  /** One-line result of the action that opened this page (e.g. a copy). */
  notice?: string | null
  onBack: () => void
  onEditComposition: () => void
  onListChanged: (priceList: PriceListRecord) => void
}

/**
 * The composition of one price list: for the chosen day, every product's base price, what each
 * rate component adds and the final price, plus the composition in force (and its history) and the
 * list's floor. Reads only; changes go through "Editar composición" and the floor select.
 */
export function PriceListBreakdownPage({
  priceList,
  priceLists,
  notice = null,
  onBack,
  onEditComposition,
  onListChanged,
}: PriceListBreakdownPageProps) {
  const { t } = useTranslation('priceLists')
  const [on, setOn] = useState(todayIso)
  const [breakdown, setBreakdown] = useState<PriceListBreakdown | null>(null)
  const [history, setHistory] = useState<CompositionRecord | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [floorError, setFloorError] = useState<string | null>(null)
  const [floorViolations, setFloorViolations] = useState<FloorViolation[] | null>(null)
  const [view, setView] = useViewPreference('price-list-breakdown', phoneDefaultView())

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    getBreakdown(priceList.id, on)
      .then((result) => {
        if (!cancelled) setBreakdown(result)
      })
      .catch((err) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : t('breakdown.errors.unexpectedLoad'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [priceList.id, on, t])

  // The history is a property of the list, not of the day being looked at.
  useEffect(() => {
    let cancelled = false
    getComposition(priceList.id)
      .then((result) => {
        if (!cancelled) setHistory(result)
      })
      .catch(() => {
        if (!cancelled) setHistory(null)
      })
    return () => {
      cancelled = true
    }
  }, [priceList.id])

  const floorList = priceLists.find((list) => list.id === priceList.floorPriceListId) ?? null
  const components = [...(breakdown?.composition.components ?? [])].sort((a, b) => a.order - b.order)

  const handleFloorChange = async (value: string) => {
    setFloorError(null)
    setFloorViolations(null)
    try {
      onListChanged(await setFloor(priceList.id, value === '' ? null : value))
    } catch (err) {
      const violations = floorViolationsOf(err)
      if (violations) setFloorViolations(violations)
      else if (err instanceof ApiError && err.status === 409 && err.code === 'floor-cycle') {
        setFloorError(t('breakdown.errors.floorCycle'))
      } else {
        setFloorError(err instanceof ApiError ? err.message : t('breakdown.errors.unexpectedFloor'))
      }
    }
  }

  const columns: DataViewColumn<BreakdownItem>[] = [
    {
      key: 'product',
      header: t('breakdown.columns.product'),
      cell: (item) => (
        <span>
          {item.productName} <span className="text-muted-foreground">· {item.presentationName}</span>
        </span>
      ),
    },
    {
      key: 'code',
      header: t('breakdown.columns.code'),
      cell: (item) => item.identificationCode ?? '—',
      hideOnMobile: true,
    },
    { key: 'base', header: t('breakdown.columns.base'), cell: (item) => formatMoney(item.base) },
    ...components.map((component) => ({
      key: `component-${component.code}`,
      header: `${component.label} ${formatPercent(component.percentage)} %`,
      cell: (item: BreakdownItem) => {
        const line = item.components.find((candidate) => candidate.code === component.code)
        return line ? formatMoney(line.amount) : '—'
      },
    })),
    {
      key: 'final',
      header: t('breakdown.columns.final'),
      cell: (item) => <span className="font-semibold">{formatMoney(item.final)}</span>,
    },
  ]

  return (
    <FormPage
      title={t('breakdown.title', { name: priceList.name })}
      description={t('breakdown.description')}
      onBack={onBack}
      backLabel={t('breakdown.backLabel')}
    >
      <div className="flex flex-col gap-6">
        {notice && (
          <p role="status" className="rounded-md border border-border bg-muted px-4 py-3 text-sm">
            {notice}
          </p>
        )}

        <div className="flex flex-wrap items-start justify-between gap-4 rounded-lg border border-border bg-card p-4">
          <div className="flex min-w-0 flex-col gap-1">
            <p className="text-sm font-medium break-words">
              {summarizeComponents(breakdown?.composition.components ?? [], t)}
            </p>
            <p className="text-sm text-muted-foreground">
              {floorList ? t('breakdown.floor', { name: floorList.name }) : t('breakdown.noFloor')}
            </p>
          </div>
          <Button type="button" onClick={onEditComposition}>
            {t('breakdown.editComposition')}
          </Button>
        </div>

        <div className="flex flex-wrap items-end gap-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="breakdown-on">{t('breakdown.dateLabel')}</Label>
            <Input
              id="breakdown-on"
              type="date"
              value={on}
              onChange={(e) => {
                if (e.target.value !== '') setOn(e.target.value)
              }}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="breakdown-floor">{t('breakdown.floorSelect')}</Label>
            <Select
              id="breakdown-floor"
              value={priceList.floorPriceListId ?? ''}
              onChange={(e) => void handleFloorChange(e.target.value)}
            >
              <option value="">{t('breakdown.noFloor')}</option>
              {priceLists
                .filter((list) => list.id !== priceList.id)
                .map((list) => (
                  <option key={list.id} value={list.id}>
                    {list.name}
                  </option>
                ))}
            </Select>
          </div>
          <div className="ml-auto">
            <ViewSwitch value={view} onChange={setView} />
          </div>
        </div>
        <p className="-mt-4 text-xs text-muted-foreground">{t('breakdown.floorHint')}</p>

        {floorError && (
          <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {floorError}
          </p>
        )}
        {floorViolations && <FloorViolationsTable violations={floorViolations} />}

        {loadError && (
          <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {loadError}
          </p>
        )}

        <DataView
          items={breakdown?.items ?? []}
          columns={columns}
          getRowKey={(item) => item.presentationId}
          view={view}
          loading={loading}
          loadingMessage={t('breakdown.loading')}
          emptyMessage={t('breakdown.empty')}
          loadErrorMessage={loadError ? t('breakdown.errors.unexpectedLoad') : null}
        />

        <section className="flex flex-col gap-2">
          <h2 className="text-sm font-semibold">{t('breakdown.historyTitle')}</h2>
          {history && history.history.length > 0 ? (
            <ul className="flex flex-col gap-1 text-sm">
              {history.history.map((version) => (
                <li key={version.id}>
                  {t('breakdown.historyLine', {
                    date: formatIsoDate(version.effectiveFrom),
                    summary: summarizeComponents(version.components, t),
                  })}
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-muted-foreground">{t('breakdown.historyEmpty')}</p>
          )}
        </section>
      </div>
    </FormPage>
  )
}
