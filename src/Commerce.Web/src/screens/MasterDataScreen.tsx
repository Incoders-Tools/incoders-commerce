import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import type { MasterDataApi } from '@/api/masterData'
import type { MasterDataEntry } from '@/api/types'

export interface MasterDataScreenProps {
  /** i18n namespace holding this catalog's copy (`cities`, `businessTypes`). */
  namespace: string
  api: MasterDataApi
  /** localStorage key of the table/cards preference. */
  viewKey: string
  /** Server error codes (`{ error }` of a 409) for a duplicate name / key. */
  nameInUseCode: string
  keyInUseCode: string
}

type StatusFilter = 'all' | 'active' | 'inactive'

const dateFormat = new Intl.DateTimeFormat('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric' })
const formatDate = (iso: string) => dateFormat.format(new Date(iso))

/**
 * Shared ABM screen for the organization-scoped customer catalogs (cities,
 * business types): list with search and active/inactive filter, full-page
 * create/edit form, and activate/deactivate (the API has no DELETE). Copy
 * lives in the namespace the caller names; the server owns permissions and
 * uniqueness, this screen only reports those outcomes in Spanish.
 */
export function MasterDataScreen({ namespace, api, viewKey, nameInUseCode, keyInUseCode }: MasterDataScreenProps) {
  const { t } = useTranslation(namespace)
  const [entries, setEntries] = useState<MasterDataEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<StatusFilter>('all')
  const [view, setView] = useViewPreference(viewKey)
  const [editing, setEditing] = useState<MasterDataEntry | 'new' | null>(null)

  const refresh = useCallback(async () => {
    try {
      setEntries(await api.list(true))
      setLoadError(null)
    } catch {
      setLoadError(t('errors.unableToLoad'))
    } finally {
      setLoading(false)
    }
  }, [api, t])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const handleToggleActive = async (entry: MasterDataEntry) => {
    setActionError(null)
    try {
      await api.update(entry.id, {
        name: entry.name,
        key: entry.key,
        sortOrder: entry.sortOrder,
        isActive: !entry.isActive,
      })
      await refresh()
    } catch {
      setActionError(t('errors.unableToUpdate'))
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleEntries = useMemo(
    () =>
      entries.filter((entry) => {
        if (status === 'active' && !entry.isActive) return false
        if (status === 'inactive' && entry.isActive) return false
        return (
          trimmedSearch === '' ||
          entry.name.toLowerCase().includes(trimmedSearch) ||
          entry.key.toLowerCase().includes(trimmedSearch)
        )
      }),
    [entries, status, trimmedSearch],
  )

  if (editing !== null) {
    return (
      <MasterDataForm
        namespace={namespace}
        api={api}
        entry={editing === 'new' ? null : editing}
        nameInUseCode={nameInUseCode}
        keyInUseCode={keyInUseCode}
        onCancel={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null)
          await refresh()
        }}
      />
    )
  }

  const columns: DataViewColumn<MasterDataEntry>[] = [
    { key: 'name', header: t('columns.name'), cell: (entry) => entry.name },
    {
      key: 'key',
      header: t('columns.key'),
      cell: (entry) => <span className="text-muted-foreground">{entry.key}</span>,
      hideOnMobile: true,
    },
    { key: 'sortOrder', header: t('columns.sortOrder'), cell: (entry) => entry.sortOrder, hideOnMobile: true },
    {
      key: 'status',
      header: t('columns.status'),
      cell: (entry) => (entry.isActive ? t('status.active') : t('status.inactive')),
    },
    { key: 'createdAt', header: t('columns.createdAt'), cell: (entry) => formatDate(entry.createdAtUtc), hideOnMobile: true },
    { key: 'updatedAt', header: t('columns.updatedAt'), cell: (entry) => formatDate(entry.updatedAtUtc), hideOnMobile: true },
  ]

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
          aria-label={t('status.filterLabel')}
          className="w-auto"
          value={status}
          onChange={(event) => setStatus(event.target.value as StatusFilter)}
        >
          <option value="all">{t('status.all')}</option>
          <option value="active">{t('status.active')}</option>
          <option value="inactive">{t('status.inactive')}</option>
        </Select>
      </DataToolbar>

      <DataView
        items={visibleEntries}
        columns={columns}
        getRowKey={(entry) => entry.id}
        view={view}
        loading={loading}
        emptyMessage={entries.length === 0 ? t('empty.none') : t('empty.noMatch')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(entry) => (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => setEditing(entry)}>
              {t('actions.edit')}
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={() => void handleToggleActive(entry)}>
              {entry.isActive ? t('actions.deactivate') : t('actions.activate')}
            </Button>
          </>
        )}
      />
    </section>
  )
}

function MasterDataForm({
  namespace,
  api,
  entry,
  nameInUseCode,
  keyInUseCode,
  onCancel,
  onSaved,
}: {
  namespace: string
  api: MasterDataApi
  entry: MasterDataEntry | null
  nameInUseCode: string
  keyInUseCode: string
  onCancel: () => void
  onSaved: () => Promise<void>
}) {
  const { t } = useTranslation(namespace)
  const [name, setName] = useState(entry?.name ?? '')
  const [key, setKey] = useState(entry?.key ?? '')
  const [sortOrder, setSortOrder] = useState(entry ? String(entry.sortOrder) : '')
  const [isActive, setIsActive] = useState(entry?.isActive ?? true)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      // Blank key / order are omitted so the server generates (create) or
      // keeps (edit) them; isActive is always sent because an omitted one
      // would silently reactivate the entry.
      const request = {
        name: name.trim(),
        ...(key.trim() !== '' ? { key: key.trim() } : {}),
        ...(sortOrder.trim() !== '' ? { sortOrder: Number(sortOrder) } : {}),
        isActive,
      }
      if (entry) {
        await api.update(entry.id, request)
      } else {
        await api.create(request)
      }
      await onSaved()
    } catch (err) {
      const code = err instanceof ApiError && err.status === 409 ? err.code : undefined
      setError(
        code === nameInUseCode ? t('errors.nameInUse') : code === keyInUseCode ? t('errors.keyInUse') : t('errors.unableToSave'),
      )
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={entry ? t('form.editTitle') : t('form.createTitle')}
      description={t('form.description')}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex flex-col gap-6" onSubmit={handleSubmit}>
        <div className="grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2 xl:grid-cols-3">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="masterName">{t('form.name')}</Label>
            <Input id="masterName" value={name} onChange={(e) => setName(e.target.value)} required />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="masterKey">{t('form.key')}</Label>
            <Input id="masterKey" value={key} onChange={(e) => setKey(e.target.value)} />
            <p className="text-xs text-muted-foreground">{t('form.keyHint')}</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="masterSortOrder">{t('form.sortOrder')}</Label>
            <Input
              id="masterSortOrder"
              type="number"
              value={sortOrder}
              onChange={(e) => setSortOrder(e.target.value)}
            />
          </div>
          <div className="flex items-center gap-2">
            <input
              id="masterIsActive"
              type="checkbox"
              checked={isActive}
              onChange={(e) => setIsActive(e.target.checked)}
            />
            <Label htmlFor="masterIsActive">{t('form.isActive')}</Label>
          </div>
          {entry && (
            <div className="flex flex-col gap-1 text-sm text-muted-foreground md:col-span-2">
              <p>{t('form.createdAt', { date: formatDate(entry.createdAtUtc) })}</p>
              <p>{t('form.updatedAt', { date: formatDate(entry.updatedAtUtc) })}</p>
            </div>
          )}
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('form.saving') : t('form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
