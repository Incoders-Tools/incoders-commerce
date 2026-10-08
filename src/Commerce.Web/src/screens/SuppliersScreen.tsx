import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { CityPicker } from '@/components/geo/CityPicker'
import { cityLabel, type CityOption } from '@/components/geo/cityLabel'
import { SupplierForm } from './SupplierForm'
import { supplierCategoriesApi } from '@/api/supplierCategories'
import { listSupplierBalances, listSuppliers } from '@/api/suppliers'
import { ApiError } from '@/api/client'
import type { MasterDataEntry, SupplierBalance, SupplierRecord } from '@/api/types'
import { formatMoney } from '@/dashboard/format'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { cn } from '@/lib/utils'

const SEARCH_DEBOUNCE_MS = 300

type EnabledFilter = 'all' | 'enabled' | 'disabled'

/**
 * Suppliers list + full-page create/edit, modelled on `CustomersScreen`.
 * Search, category, city and enabled state are applied by the server
 * (`GET /suppliers?search&categoryId&cityId&enabled`); the search box is
 * debounced. Balances (what we owe, and how much of it is overdue) come from
 * one `GET /suppliers/account/balances` call per refresh; if that fails the
 * list still renders with the balance each supplier carries.
 */
export function SuppliersScreen() {
  const { t } = useTranslation('suppliers')
  const [suppliers, setSuppliers] = useState<SupplierRecord[]>([])
  const [balances, setBalances] = useState<Map<string, SupplierBalance>>(new Map())
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [editing, setEditing] = useState<SupplierRecord | null>(null)
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState('')
  const [cityFilter, setCityFilter] = useState<CityOption | null>(null)
  const cityId = cityFilter?.id ?? ''
  const [categoryId, setCategoryId] = useState('')
  const [enabledFilter, setEnabledFilter] = useState<EnabledFilter>('all')
  const [categories, setCategories] = useState<MasterDataEntry[]>([])
  const [view, setView] = useViewPreference('suppliers')
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  /** Only the latest request may write state. */
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    const enabled = enabledFilter === 'all' ? undefined : enabledFilter === 'enabled'
    try {
      const [result, balanceRows] = await Promise.all([
        listSuppliers({ search: debouncedSearch, categoryId, cityId, enabled }),
        listSupplierBalances().catch(() => [] as SupplierBalance[]),
      ])
      if (request === latestRequest.current) {
        setSuppliers(result)
        setBalances(new Map(balanceRows.map((row) => [row.supplierId, row])))
      }
    } catch (err) {
      if (request === latestRequest.current) {
        setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
      }
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, debouncedSearch, categoryId, cityId, enabledFilter])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Categories only feed a select and the form: if they cannot be read the screen still works.
  useEffect(() => {
    supplierCategoriesApi.list(true).then(setCategories, () => setCategories([]))
  }, [])

  const closeForm = () => {
    setCreating(false)
    setEditing(null)
  }

  const filtering = debouncedSearch !== '' || cityId !== '' || categoryId !== '' || enabledFilter !== 'all'
  const activeCategories = useMemo(() => categories.filter((category) => category.isActive), [categories])

  if (creating || editing !== null) {
    return (
      <SupplierForm
        // A reload swaps in fresher data: remount so the form state restarts from it.
        key={editing ? `${editing.id}:${editing.updatedAtUtc}` : 'new'}
        supplier={editing ?? undefined}
        categories={categories}
        onSaved={() => {
          closeForm()
          void refresh()
        }}
        onCancel={closeForm}
        onReload={setEditing}
      />
    )
  }

  const noValue = <span className="text-muted-foreground">{t('columns.noValue')}</span>
  const columns: DataViewColumn<SupplierRecord>[] = [
    { key: 'displayName', header: t('columns.name'), cell: (supplier) => supplier.displayName },
    { key: 'category', header: t('columns.category'), cell: (supplier) => supplier.categoryName ?? noValue },
    {
      key: 'contact',
      header: t('columns.contact'),
      cell: (supplier) => {
        const contact = supplier.contacts.find((c) => c.isPrimary) ?? supplier.contacts[0]
        return contact ? [contact.firstName, contact.lastName].filter(Boolean).join(' ') : noValue
      },
      hideOnMobile: true,
    },
    {
      key: 'city',
      header: t('columns.city'),
      cell: (supplier) =>
        supplier.cityName ? cityLabel({ name: supplier.cityName, provinceName: supplier.provinceName ?? '' }) : noValue,
      hideOnMobile: true,
    },
    {
      key: 'taxId',
      header: t('columns.taxId'),
      cell: (supplier) => supplier.taxId ?? <span className="text-muted-foreground">{t('columns.noTaxId')}</span>,
      hideOnMobile: true,
    },
    {
      key: 'balance',
      header: t('columns.balance'),
      headerHint: t('columns.balanceHint'),
      cell: (supplier) => {
        const row = balances.get(supplier.id)
        const balance = row?.balance ?? supplier.balance
        const overdue = row?.overdue ?? 0
        return (
          <span className="flex flex-col">
            <span className={cn(balance < 0 && 'text-muted-foreground')}>
              {balance < 0 ? t('balance.inOurFavour', { amount: formatMoney(-balance) }) : formatMoney(balance)}
            </span>
            {overdue > 0 && (
              <span className="text-xs font-semibold text-destructive">
                {t('balance.overdue', { amount: formatMoney(overdue) })}
              </span>
            )}
          </span>
        )
      },
    },
    {
      key: 'isEnabled',
      header: t('columns.status'),
      cell: (supplier) => (supplier.isEnabled ? t('statusOptions.enabled') : t('statusOptions.disabled')),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={<Button onClick={() => setCreating(true)}>{t('newSupplier')}</Button>}
      />

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
        <CityPicker
          label={t('filters.city')}
          hideLabel
          placeholder={t('filters.allCities')}
          value={cityFilter}
          onChange={setCityFilter}
          className="w-full sm:w-56"
        />
        <Select
          aria-label={t('filters.category')}
          className="w-auto"
          value={categoryId}
          onChange={(e) => setCategoryId(e.target.value)}
        >
          <option value="">{t('filters.allCategories')}</option>
          {activeCategories.map((category) => (
            <option key={category.id} value={category.id}>
              {category.name}
            </option>
          ))}
        </Select>
        <Select
          aria-label={t('filters.status')}
          className="w-auto"
          value={enabledFilter}
          onChange={(e) => setEnabledFilter(e.target.value as EnabledFilter)}
        >
          <option value="all">{t('filters.allStatuses')}</option>
          <option value="enabled">{t('statusOptions.enabled')}</option>
          <option value="disabled">{t('statusOptions.disabled')}</option>
        </Select>
      </DataToolbar>

      <DataView
        items={suppliers}
        columns={columns}
        getRowKey={(supplier) => supplier.id}
        view={view}
        loading={loading}
        emptyMessage={filtering ? t('empty.noMatch') : t('empty.none')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(supplier) => (
          <>
            <Button variant="outline" size="sm" onClick={() => setEditing(supplier)}>
              {t('actions.edit')}
            </Button>
            <Link
              to={`/app/suppliers/${supplier.id}/account`}
              className={buttonVariants({ variant: 'outline', size: 'sm' })}
            >
              {t('actions.account')}
            </Link>
          </>
        )}
      />
    </section>
  )
}
