import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { ViewSwitch } from '@/components/data/ViewSwitch'
import { useViewPreference } from '@/components/data/useViewPreference'
import { RunStatusBadge } from '@/components/fulfillment/StatusBadges'
import { fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import { listRuns, type DeliveryRunSummary, type RunStatus } from '@/api/fulfillment'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatIsoDate, formatMoney } from '@/dashboard/format'

const RUN_STATUSES: RunStatus[] = ['Planned', 'OutForDelivery', 'Completed']

/** The branch's delivery runs (repartos), newest first, filtered by status and date by the server. */
export function DeliveriesScreen() {
  const { t } = useTranslation('fulfillment')
  const missingBranch = useMissingBranch()
  const [runs, setRuns] = useState<DeliveryRunSummary[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<RunStatus | ''>('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [view, setView] = useViewPreference('delivery-runs')
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    if (missingBranch) return
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listRuns({ status, from, to })
      if (request === latestRequest.current) setRuns(result)
    } catch (err) {
      if (request === latestRequest.current) setLoadError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, missingBranch, status, from, to])

  useEffect(() => {
    void refresh()
  }, [refresh])

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('runs.title')} description={t('runs.description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  const columns: DataViewColumn<DeliveryRunSummary>[] = [
    { key: 'number', header: t('runs.columns.number'), cell: (run) => <span className="font-medium">{run.runNumber}</span> },
    { key: 'date', header: t('runs.columns.date'), cell: (run) => formatIsoDate(run.runDate) },
    { key: 'driver', header: t('runs.columns.driver'), cell: (run) => run.driverName ?? '—', hideOnMobile: true },
    { key: 'vehicle', header: t('runs.columns.vehicle'), cell: (run) => run.vehicle ?? '—', hideOnMobile: true },
    { key: 'orders', header: t('runs.columns.orders'), cell: (run) => run.orderCount },
    { key: 'total', header: t('runs.columns.total'), cell: (run) => formatMoney(run.total) },
    { key: 'status', header: t('runs.columns.status'), cell: (run) => <RunStatusBadge status={run.status} /> },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('runs.title')}
        description={t('runs.description')}
        actions={
          <Link to="/app/deliveries/new" className={buttonVariants()}>
            {t('runs.new')}
          </Link>
        }
      />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <Select aria-label={t('runs.filters.status')} className="w-auto" value={status} onChange={(e) => setStatus(e.target.value as RunStatus | '')}>
          <option value="">{t('runs.filters.all')}</option>
          {RUN_STATUSES.map((value) => (
            <option key={value} value={value}>
              {t(`runStatus.${value}`)}
            </option>
          ))}
        </Select>
        <Input type="date" aria-label={t('runs.filters.from')} className="w-auto" value={from} onChange={(e) => setFrom(e.target.value)} />
        <Input type="date" aria-label={t('runs.filters.to')} className="w-auto" value={to} onChange={(e) => setTo(e.target.value)} />
        <span className="ml-auto">
          <ViewSwitch value={view} onChange={setView} />
        </span>
      </div>

      <DataView
        items={runs}
        columns={columns}
        getRowKey={(run) => run.runId}
        view={view}
        loading={loading}
        emptyMessage={status !== '' || from !== '' || to !== '' ? t('runs.empty.noMatch') : t('runs.empty.none')}
        loadErrorMessage={loadError === null ? null : t('runs.empty.loadError')}
        renderActions={(run) => (
          <Link to={`/app/deliveries/${run.runId}`} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            {t('runs.open')}
          </Link>
        )}
      />
    </section>
  )
}
