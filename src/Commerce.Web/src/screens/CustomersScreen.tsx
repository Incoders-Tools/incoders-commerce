import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { CityPicker } from '@/components/geo/CityPicker'
import { cityLabel, type CityOption } from '@/components/geo/cityLabel'
import { CustomerForm } from './CustomerForm'
import { businessTypesApi } from '@/api/businessTypes'
import { getOwnOrganizationSettings } from '@/api/account'
import { issueOrderingAccess, listCustomers } from '@/api/customers'
import { listProvinces } from '@/api/geo'
import { listPriceLists } from '@/api/pricing'
import { ApiError } from '@/api/client'
import {
  CustomerKind,
  type CustomerRecord,
  type GeoProvince,
  type MasterDataEntry,
  type PriceListRecord,
} from '@/api/types'
import { useDebouncedValue } from '@/lib/useDebouncedValue'

const SEARCH_DEBOUNCE_MS = 300

/**
 * List + create/edit (design.md "Two admin UIs against one endpoint set").
 * Reachable only through `RequireAdmin` (App.tsx), but the server's
 * `ManageUsers` check on every `/customers` call remains the real gate.
 *
 * T4b: migrated onto the shared data-view layer (`components/data/*`),
 * following `CatalogScreen.tsx`. The old `mx-auto … max-w-3xl` Card wrapper
 * is gone — the T3 shell already owns the page frame, so this screen now
 * fills the available width.
 *
 * Customer master data: search (name, legal name, contact, tax id), city and
 * business type are applied by the server (`GET /customers?search&cityId&
 * businessTypeId`); the search box is debounced so typing does not issue one
 * request per keystroke. The two catalogs are loaded once here and handed to
 * the filters and to the form.
 */
export function CustomersScreen() {
  const { t } = useTranslation('customers')
  const [customers, setCustomers] = useState<CustomerRecord[]>([])
  const [loading, setLoading] = useState(true)
  /** Why the last load failed, if it did. Never set by an action: an action
   * failing says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [editingCustomer, setEditingCustomer] = useState<CustomerRecord | null>(null)
  const [creating, setCreating] = useState(false)
  const [issuedCredential, setIssuedCredential] = useState<{ customerId: string; credential: string } | null>(null)
  const [search, setSearch] = useState('')
  const [cityFilter, setCityFilter] = useState<CityOption | null>(null)
  const cityId = cityFilter?.id ?? ''
  const [businessTypeId, setBusinessTypeId] = useState('')
  const [businessTypes, setBusinessTypes] = useState<MasterDataEntry[]>([])
  const [provinces, setProvinces] = useState<GeoProvince[]>([])
  const [priceLists, setPriceLists] = useState<PriceListRecord[]>([])
  const [defaultPriceListId, setDefaultPriceListId] = useState<string | null>(null)
  const [priceListFilter, setPriceListFilter] = useState('')
  const [view, setView] = useViewPreference('customers')
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  /** Only the latest request may write state: a slow older answer must not overwrite a newer one. */
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listCustomers({ search: debouncedSearch, cityId, businessTypeId })
      if (request === latestRequest.current) setCustomers(result)
    } catch (err) {
      if (request === latestRequest.current) {
        setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
      }
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, debouncedSearch, cityId, businessTypeId])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // The business types only feed a select: if they cannot be read the screen
  // still works, the select just offers no choices. Cities are searched on the
  // server by the picker, never loaded whole.
  useEffect(() => {
    businessTypesApi.list(true).then(setBusinessTypes, () => setBusinessTypes([]))
  }, [])

  // The organization country's provinces feed the form's Province select; without them the form searches cities
  // across the country instead.
  useEffect(() => {
    listProvinces().then(
      (loaded) => setProvinces(Array.isArray(loaded) ? loaded : []),
      () => setProvinces([]),
    )
  }, [])

  // Like the catalogs above, the lists only feed selects: unreadable (a role without catalog access, say) means
  // the selects offer no choices, not a broken screen.
  useEffect(() => {
    listPriceLists().then(
      (lists) => setPriceLists(Array.isArray(lists) ? lists : []),
      () => setPriceLists([]),
    )
    getOwnOrganizationSettings().then(
      (settings) => setDefaultPriceListId(settings?.defaultCustomerPriceListId ?? null),
      () => setDefaultPriceListId(null),
    )
  }, [])

  const closeForm = () => {
    setCreating(false)
    setEditingCustomer(null)
  }

  const handleSaved = () => {
    closeForm()
    void refresh()
  }

  const handleIssueAccess = async (customerId: string) => {
    setActionError(null)
    try {
      const result = await issueOrderingAccess(customerId)
      setIssuedCredential({ customerId, credential: result.credential })
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t('errors.unexpectedIssueAccess'))
    }
  }

  const filtering = debouncedSearch !== '' || cityId !== '' || businessTypeId !== '' || priceListFilter !== ''
  // The list filter is applied here: the server already returns each customer's list.
  const shownCustomers =
    priceListFilter === '' ? customers : customers.filter((customer) => customer.priceListId === priceListFilter)

  // The create/edit form deliberately still replaces the whole screen, exactly
  // as before T4b — it is a long form, not an inline row edit.
  if (creating || editingCustomer !== null) {
    return (
      <CustomerForm
        // A reload swaps in fresher data: remount so the form state restarts from it.
        key={editingCustomer ? `${editingCustomer.id}:${editingCustomer.updatedAtUtc}` : 'new'}
        customer={editingCustomer ?? undefined}
        businessTypes={businessTypes}
        priceLists={priceLists}
        provinces={provinces}
        defaultPriceListId={defaultPriceListId}
        onSaved={handleSaved}
        onCancel={closeForm}
        onReload={setEditingCustomer}
      />
    )
  }

  const noValue = <span className="text-muted-foreground">{t('columns.noValue')}</span>
  const columns: DataViewColumn<CustomerRecord>[] = [
    { key: 'displayName', header: t('columns.name'), cell: (customer) => customer.displayName },
    {
      key: 'contact',
      header: t('columns.contact'),
      cell: (customer) => {
        // The primary contact, or the first one when none is marked primary.
        const contact = customer.contacts.find((c) => c.isPrimary) ?? customer.contacts[0]
        return contact ? [contact.firstName, contact.lastName].filter(Boolean).join(' ') : noValue
      },
    },
    { key: 'city', header: t('columns.city'), cell: (customer) =>
        customer.cityName ? cityLabel({ name: customer.cityName, provinceName: customer.provinceName ?? '' }) : noValue,
    },
    {
      key: 'businessType',
      header: t('columns.businessType'),
      cell: (customer) => customer.businessTypeName ?? noValue,
      hideOnMobile: true,
    },
    {
      key: 'priceList',
      header: t('columns.priceList'),
      cell: (customer) => customer.priceListName ?? noValue,
      hideOnMobile: true,
    },
    {
      key: 'phone',
      header: t('columns.phone'),
      cell: (customer) => customer.phone ?? noValue,
      hideOnMobile: true,
    },
    {
      key: 'taxId',
      header: t('columns.taxId'),
      cell: (customer) =>
        customer.taxId ?? <span className="text-muted-foreground">{t('columns.noTaxId')}</span>,
      hideOnMobile: true,
    },
    {
      key: 'customerKind',
      header: t('columns.kind'),
      cell: (customer) => t(`kindOptions.${customer.customerKind === CustomerKind.Wholesale ? 'wholesale' : 'retail'}`),
      hideOnMobile: true,
    },
    {
      key: 'isEnabled',
      header: t('columns.status'),
      cell: (customer) => (customer.isEnabled ? t('statusOptions.enabled') : t('statusOptions.disabled')),
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={<Button onClick={() => setCreating(true)}>{t('newCustomer')}</Button>}
      />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      {actionError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {actionError}
        </p>
      )}

      {issuedCredential && (
        <p
          data-testid="issued-credential"
          className="rounded-md border border-border bg-muted px-4 py-3 text-sm text-foreground"
        >
          {t('issuedCredential', { credential: issuedCredential.credential })}
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
          className="w-full sm:w-64"
        />
        <Select
          aria-label={t('filters.businessType')}
          className="w-auto"
          value={businessTypeId}
          onChange={(e) => setBusinessTypeId(e.target.value)}
        >
          <option value="">{t('filters.allBusinessTypes')}</option>
          {businessTypes.map((businessType) => (
            <option key={businessType.id} value={businessType.id}>
              {businessType.name}
            </option>
          ))}
        </Select>
        <Select
          aria-label={t('filters.priceList')}
          className="w-auto"
          value={priceListFilter}
          onChange={(e) => setPriceListFilter(e.target.value)}
        >
          <option value="">{t('filters.allPriceLists')}</option>
          {priceLists.map((priceList) => (
            <option key={priceList.id} value={priceList.id}>
              {priceList.name}
            </option>
          ))}
        </Select>
      </DataToolbar>

      <DataView
        items={shownCustomers}
        columns={columns}
        getRowKey={(customer) => customer.id}
        view={view}
        loading={loading}
        emptyMessage={filtering ? t('empty.noMatch') : t('empty.none')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(customer) => (
          <>
            <Button variant="outline" size="sm" onClick={() => setEditingCustomer(customer)}>
              {t('actions.edit')}
            </Button>
            <Button variant="outline" size="sm" onClick={() => void handleIssueAccess(customer.id)}>
              {t('actions.issueAccess')}
            </Button>
          </>
        )}
      />
    </section>
  )
}
