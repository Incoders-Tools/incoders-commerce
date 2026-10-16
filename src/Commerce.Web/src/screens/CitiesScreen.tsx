import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { listCities, listProvinces, updateCity } from '@/api/geo'
import type { GeoCity, GeoProvince } from '@/api/types'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { CityForm } from './CityForm'

const PAGE_SIZE = 25
const SEARCH_DEBOUNCE_MS = 300

type StatusFilter = 'all' | 'active'

const dateFormat = new Intl.DateTimeFormat('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric' })
const formatDate = (iso: string) => dateFormat.format(new Date(iso))

/**
 * Core city catalog ABM, system administrator only (`RequireSystemAdmin` in
 * App.tsx; the server answers 403 to anyone else). The catalog holds ~4000
 * cities shared by every organization, so the list is searched, filtered and
 * paged by the server (`GET /geo/cities`), never loaded whole. "Load more"
 * appends the next page; a new search or filter starts again at the first.
 * The status filter is "active only" or "include inactive" because the API
 * has no inactive-only query.
 */
export function CitiesScreen() {
  const { t } = useTranslation('cities')
  const [cities, setCities] = useState<GeoCity[]>([])
  const [provinces, setProvinces] = useState<GeoProvince[]>([])
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [hasMore, setHasMore] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [provinceId, setProvinceId] = useState('')
  const [status, setStatus] = useState<StatusFilter>('all')
  const [view, setView] = useViewPreference('cities')
  const [editing, setEditing] = useState<GeoCity | 'new' | null>(null)
  const debouncedSearch = useDebouncedValue(search.trim(), SEARCH_DEBOUNCE_MS)
  /** Only the latest request may write state: a slow older answer must not overwrite a newer one. */
  const latestRequest = useRef(0)

  const filters = useCallback(
    (offset: number) => ({
      search: debouncedSearch,
      provinceId,
      limit: PAGE_SIZE,
      offset,
      includeInactive: status === 'all',
    }),
    [debouncedSearch, provinceId, status],
  )

  const refresh = useCallback(async () => {
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const page = await listCities(filters(0))
      if (request !== latestRequest.current) return
      setCities(page)
      setHasMore(page.length === PAGE_SIZE)
    } catch {
      if (request === latestRequest.current) setLoadError(t('errors.unableToLoad'))
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [filters, t])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Provinces only feed selects: if they cannot be read the screen still lists cities.
  useEffect(() => {
    listProvinces().then(setProvinces, () => setProvinces([]))
  }, [])

  const handleLoadMore = async () => {
    const request = latestRequest.current
    setLoadingMore(true)
    try {
      const page = await listCities(filters(cities.length))
      if (request !== latestRequest.current) return
      setCities((current) => [...current, ...page])
      setHasMore(page.length === PAGE_SIZE)
    } catch {
      if (request === latestRequest.current) setActionError(t('errors.unableToLoad'))
    } finally {
      setLoadingMore(false)
    }
  }

  const handleToggleActive = async (city: GeoCity) => {
    setActionError(null)
    try {
      const saved = await updateCity(city.id, { name: city.name, isActive: !city.isActive })
      setCities((current) => current.map((entry) => (entry.id === saved.id ? saved : entry)))
    } catch {
      setActionError(t('errors.unableToUpdate'))
    }
  }

  if (editing !== null) {
    return (
      <CityForm
        city={editing === 'new' ? null : editing}
        provinces={provinces}
        onCancel={() => setEditing(null)}
        onSaved={() => {
          setEditing(null)
          void refresh()
        }}
      />
    )
  }

  const columns: DataViewColumn<GeoCity>[] = [
    { key: 'name', header: t('columns.name'), cell: (city) => city.name },
    { key: 'province', header: t('columns.province'), cell: (city) => city.provinceName },
    {
      key: 'department',
      header: t('columns.department'),
      cell: (city) => city.departmentName ?? <span className="text-muted-foreground">—</span>,
      hideOnMobile: true,
    },
    {
      key: 'postalCode',
      header: t('columns.postalCode'),
      cell: (city) => city.postalCode ?? <span className="text-muted-foreground">—</span>,
      hideOnMobile: true,
    },
    {
      key: 'indecId',
      header: t('columns.indecId'),
      cell: (city) => city.indecId ?? <span className="text-muted-foreground">—</span>,
      hideOnMobile: true,
    },
    {
      key: 'status',
      header: t('columns.status'),
      cell: (city) => (city.isActive ? t('status.active') : t('status.inactive')),
    },
    { key: 'updatedAt', header: t('columns.updatedAt'), cell: (city) => formatDate(city.updatedAtUtc), hideOnMobile: true },
  ]

  const filtering = debouncedSearch !== '' || provinceId !== ''

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          <Button type="button" onClick={() => setEditing('new')}>
            {t('actions.create')}
          </Button>
        }
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

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel={t('search.label')}
        searchPlaceholder={t('search.placeholder')}
        view={view}
        onViewChange={setView}
      >
        <Select
          aria-label={t('filters.province')}
          className="w-auto"
          value={provinceId}
          onChange={(event) => setProvinceId(event.target.value)}
        >
          <option value="">{t('filters.allProvinces')}</option>
          {provinces.map((province) => (
            <option key={province.id} value={province.id}>
              {province.name}
            </option>
          ))}
        </Select>
        <Select
          aria-label={t('status.filterLabel')}
          className="w-auto"
          value={status}
          onChange={(event) => setStatus(event.target.value as StatusFilter)}
        >
          <option value="all">{t('status.includeInactive')}</option>
          <option value="active">{t('status.onlyActive')}</option>
        </Select>
      </DataToolbar>

      <DataView
        items={cities}
        columns={columns}
        getRowKey={(city) => city.id}
        view={view}
        loading={loading}
        emptyMessage={filtering ? t('empty.noMatch') : t('empty.none')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(city) => (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => setEditing(city)}>
              {t('actions.edit')}
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={() => void handleToggleActive(city)}>
              {city.isActive ? t('actions.deactivate') : t('actions.activate')}
            </Button>
          </>
        )}
      />

      {hasMore && !loading && (
        <div className="flex justify-center">
          <Button type="button" variant="outline" disabled={loadingMore} onClick={() => void handleLoadMore()}>
            {t('actions.loadMore')}
          </Button>
        </div>
      )}
    </section>
  )
}
