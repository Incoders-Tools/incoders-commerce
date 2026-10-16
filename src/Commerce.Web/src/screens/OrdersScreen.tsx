import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { OrderStatusBadge } from '@/components/fulfillment/StatusBadges'
import { formatDateTime, fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import { listOrders, ORDER_STATUSES, type OrderStatus, type OrderTrackingSummary } from '@/api/fulfillment'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'

const SEARCH_DEBOUNCE_MS = 300

/** Orders that can still take a remito or join a run (the server re-checks both). */
const canJoinRun = (order: OrderTrackingSummary) =>
  ['Confirmed', 'InPreparation', 'ReadyToDispatch'].includes(order.status) && order.runId === null

/**
 * The branch's orders, newest first, for following them up: "En curso" by default (not delivered nor cancelled), any
 * status, a date range and a search by number or customer, all applied by the server. Orders can be selected to print
 * their remitos in one go (`/print/remitos`) or to build a delivery run with them. Taking a new order is one click away.
 */
export function OrdersScreen() {
  const { t } = useTranslation('fulfillment')
  const navigate = useNavigate()
  const missingBranch = useMissingBranch()
  const [orders, setOrders] = useState<OrderTrackingSummary[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<OrderStatus | 'Active' | ''>('Active')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [view, setView] = useViewPreference('orders-tracking')
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    if (missingBranch) return
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listOrders({ status, from, to, search: debouncedSearch })
      if (request === latestRequest.current) {
        setOrders(result)
        setSelected((current) => new Set([...current].filter((id) => result.some((order) => order.orderId === id))))
      }
    } catch (err) {
      if (request === latestRequest.current) setLoadError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, missingBranch, status, from, to, debouncedSearch])

  useEffect(() => {
    void refresh()
  }, [refresh])

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('orders.title')} description={t('orders.description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  const toggle = (orderId: string) =>
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(orderId)) next.delete(orderId)
      else next.add(orderId)
      return next
    })
  const allSelected = orders.length > 0 && orders.every((order) => selected.has(order.orderId))
  const selectedOrders = orders.filter((order) => selected.has(order.orderId))
  const printable = selectedOrders.filter((order) => order.status !== 'Cancelled')
  const runnable = selectedOrders.filter(canJoinRun)
  const noValue = <span className="text-muted-foreground">{t('orders.columns.noValue')}</span>

  const columns: DataViewColumn<OrderTrackingSummary>[] = [
    {
      key: 'select',
      header: t('orders.columns.select'),
      hideInCards: true,
      cell: (order) => (
        <input
          type="checkbox"
          className="size-4 accent-primary"
          aria-label={`${t('orders.columns.select')} ${order.orderNumber}`}
          checked={selected.has(order.orderId)}
          onChange={() => toggle(order.orderId)}
        />
      ),
    },
    { key: 'number', header: t('orders.columns.number'), cell: (order) => <span className="font-medium">{order.orderNumber}</span> },
    { key: 'date', header: t('orders.columns.date'), cell: (order) => formatDateTime(order.submittedAtUtc), hideOnMobile: true },
    { key: 'customer', header: t('orders.columns.customer'), cell: (order) => order.customerName },
    { key: 'status', header: t('orders.columns.status'), cell: (order) => <OrderStatusBadge status={order.status} /> },
    {
      key: 'total',
      header: t('orders.columns.total'),
      cell: (order) => formatMoney(order.deliveredTotal ?? order.total),
    },
    {
      key: 'run',
      header: t('orders.columns.run'),
      hideOnMobile: true,
      cell: (order) =>
        order.runId && order.runNumber !== null ? (
          <Link to={`/app/deliveries/${order.runId}`} className="underline-offset-2 hover:underline">
            {t('orders.runLabel', { number: order.runNumber, date: order.runDate ? formatIsoDate(order.runDate) : '' })}
          </Link>
        ) : (
          noValue
        ),
    },
    { key: 'remito', header: t('orders.columns.remito'), cell: (order) => order.remitoNumber ?? noValue, hideOnMobile: true },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('orders.title')}
        description={t('orders.description')}
        actions={
          <Link to="/app/orders/new" className={buttonVariants()}>
            {t('orders.new')}
          </Link>
        }
      />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel={t('orders.search.label')}
        searchPlaceholder={t('orders.search.placeholder')}
        view={view}
        onViewChange={setView}
        className="sm:flex-wrap"
      >
        <Select
          aria-label={t('orders.filters.status')}
          className="w-auto"
          value={status}
          onChange={(e) => setStatus(e.target.value as OrderStatus | 'Active' | '')}
        >
          <option value="Active">{t('orders.filters.active')}</option>
          <option value="">{t('orders.filters.all')}</option>
          {ORDER_STATUSES.map((value) => (
            <option key={value} value={value}>
              {t(`status.${value}`)}
            </option>
          ))}
        </Select>
        <Input type="date" aria-label={t('orders.filters.from')} className="w-auto" value={from} onChange={(e) => setFrom(e.target.value)} />
        <Input type="date" aria-label={t('orders.filters.to')} className="w-auto" value={to} onChange={(e) => setTo(e.target.value)} />
      </DataToolbar>

      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card px-4 py-2 text-sm">
        <label className="inline-flex items-center gap-2">
          <input
            type="checkbox"
            className="size-4 accent-primary"
            checked={allSelected}
            disabled={orders.length === 0}
            onChange={() => setSelected(allSelected ? new Set() : new Set(orders.map((order) => order.orderId)))}
          />
          {t('orders.columns.selectAll')}
        </label>
        <span className="text-muted-foreground">{t('orders.selected', { count: selected.size })}</span>
        <span className="ml-auto inline-flex flex-wrap gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={printable.length === 0}
            onClick={() => navigate(`/print/remitos?orders=${printable.map((order) => order.orderId).join(',')}`)}
          >
            {t('orders.bulk.printRemitos')}
          </Button>
          <Button
            type="button"
            size="sm"
            disabled={runnable.length === 0}
            onClick={() => navigate(`/app/deliveries/new?orders=${runnable.map((order) => order.orderId).join(',')}`)}
          >
            {t('orders.bulk.newRun')}
          </Button>
          {selected.size > 0 && (
            <Button type="button" variant="outline" size="sm" onClick={() => setSelected(new Set())}>
              {t('orders.bulk.clear')}
            </Button>
          )}
        </span>
      </div>

      <DataView
        items={orders}
        columns={columns}
        getRowKey={(order) => order.orderId}
        view={view}
        loading={loading}
        emptyMessage={debouncedSearch !== '' || status !== '' || from !== '' || to !== '' ? t('orders.empty.noMatch') : t('orders.empty.none')}
        loadErrorMessage={loadError === null ? null : t('orders.empty.loadError')}
        renderActions={(order) => (
          <Link to={`/app/orders/${order.orderId}`} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            {t('orders.open')}
          </Link>
        )}
      />
    </section>
  )
}
