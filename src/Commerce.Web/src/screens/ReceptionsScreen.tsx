import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { ReceptionStatusBadge } from '@/components/purchasing/ReceptionStatusBadge'
import { listReceptions } from '@/api/purchases'
import { listSuppliers } from '@/api/suppliers'
import { ApiError } from '@/api/client'
import type { ReceptionStatus, ReceptionSummary, SupplierRecord } from '@/api/types'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'

const SEARCH_DEBOUNCE_MS = 300
const STATUSES: ReceptionStatus[] = ['Draft', 'Confirmed', 'Voided']

/**
 * Goods receptions of the selected branch. Status, supplier, date range and
 * search are applied by the server (`GET /purchases/receptions`); the search
 * box is debounced and only the latest request may write state.
 */
export function ReceptionsScreen() {
  const { t } = useTranslation('purchases')
  const missingBranch = useMissingBranch()
  const [receptions, setReceptions] = useState<ReceptionSummary[]>([])
  const [suppliers, setSuppliers] = useState<SupplierRecord[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<ReceptionStatus | ''>('')
  const [supplierId, setSupplierId] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('receptions')
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    if (missingBranch) return
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listReceptions({
        status: status || undefined,
        supplierId,
        from,
        to,
        search: debouncedSearch,
      })
      if (request === latestRequest.current) setReceptions(result)
    } catch (err) {
      if (request === latestRequest.current) {
        setLoadError(err instanceof ApiError ? err.message : t('list.errors.unexpectedLoad'))
      }
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, missingBranch, status, supplierId, from, to, debouncedSearch])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Suppliers only feed a filter: if they cannot be read the screen still works.
  useEffect(() => {
    if (missingBranch) return
    listSuppliers().then(setSuppliers, () => setSuppliers([]))
  }, [missingBranch])

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('list.title')} description={t('list.description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  const filtering = debouncedSearch !== '' || status !== '' || supplierId !== '' || from !== '' || to !== ''
  const noValue = <span className="text-muted-foreground">{t('list.columns.noValue')}</span>
  const columns: DataViewColumn<ReceptionSummary>[] = [
    {
      key: 'number',
      header: t('list.columns.number'),
      cell: (row) => row.number ?? <span className="text-muted-foreground">{t('list.columns.noNumber')}</span>,
    },
    { key: 'supplier', header: t('list.columns.supplier'), cell: (row) => row.supplierName },
    { key: 'document', header: t('list.columns.document'), cell: (row) => row.documentReference ?? noValue },
    { key: 'date', header: t('list.columns.date'), cell: (row) => formatIsoDate(row.occurredOn), hideOnMobile: true },
    {
      key: 'due',
      header: t('list.columns.due'),
      cell: (row) => (row.dueOn ? formatIsoDate(row.dueOn) : noValue),
      hideOnMobile: true,
    },
    { key: 'total', header: t('list.columns.total'), cell: (row) => formatMoney(row.totalAmount) },
    { key: 'status', header: t('list.columns.status'), cell: (row) => <ReceptionStatusBadge status={row.status} /> },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('list.title')}
        description={t('list.description')}
        actions={
          <Link to="/app/receptions/new" className={buttonVariants()}>
            {t('list.new')}
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
        searchLabel={t('list.search.label')}
        searchPlaceholder={t('list.search.placeholder')}
        view={view}
        onViewChange={setView}
        className="sm:flex-wrap"
      >
        <Select
          aria-label={t('list.filters.status')}
          className="w-auto"
          value={status}
          onChange={(e) => setStatus(e.target.value as ReceptionStatus | '')}
        >
          <option value="">{t('list.filters.allStatuses')}</option>
          {STATUSES.map((value) => (
            <option key={value} value={value}>
              {t(`status.${value}`)}
            </option>
          ))}
        </Select>
        <Select
          aria-label={t('list.filters.supplier')}
          className="w-auto"
          value={supplierId}
          onChange={(e) => setSupplierId(e.target.value)}
        >
          <option value="">{t('list.filters.allSuppliers')}</option>
          {suppliers.map((supplier) => (
            <option key={supplier.id} value={supplier.id}>
              {supplier.displayName}
            </option>
          ))}
        </Select>
        <Input
          type="date"
          aria-label={t('list.filters.from')}
          className="w-auto"
          value={from}
          onChange={(e) => setFrom(e.target.value)}
        />
        <Input
          type="date"
          aria-label={t('list.filters.to')}
          className="w-auto"
          value={to}
          onChange={(e) => setTo(e.target.value)}
        />
      </DataToolbar>

      <DataView
        items={receptions}
        columns={columns}
        getRowKey={(row) => row.id}
        view={view}
        loading={loading}
        emptyMessage={filtering ? t('list.empty.noMatch') : t('list.empty.none')}
        loadErrorMessage={loadError === null ? null : t('list.empty.loadError')}
        renderActions={(row) => (
          <Link to={`/app/receptions/${row.id}`} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            {t('list.open')}
          </Link>
        )}
      />
    </section>
  )
}
