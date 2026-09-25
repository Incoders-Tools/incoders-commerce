import { useEffect, useMemo, useState } from 'react'
import { listOrganizations } from '@/api/account'
import { ApiError } from '@/api/client'
import type { OrganizationSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { OrganizationForm } from './OrganizationForm'

function formatCreatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString()
}

/**
 * T8: reformatted from a single minified line with no layout classes at all
 * (fields literally overlapped visually) onto the shared data-view layer,
 * following `BranchesScreen.tsx` / `CustomersScreen.tsx`. The create form
 * moved behind a "New organization" action and now renders full-width in
 * place of the list, mirroring the `CustomersScreen` → `CustomerForm`
 * state-swap. Settings fields (logo, theme colors, date format, geolocation,
 * usage plan) are NOT here — T5 rebuilds this screen again once a spec
 * exists for them (see the task file: no such field exists on `Organization`
 * today). Reachable only through `RequireSystemAdmin` (App.tsx).
 */
export function OrganizationsScreen() {
  const [organizations, setOrganizations] = useState<OrganizationSummary[]>([])
  const [loading, setLoading] = useState(true)
  /** Why the last load failed, if it did. Never set by the create form: a
   * failed create says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('organizations')

  const refresh = async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setOrganizations(await listOrganizations())
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : 'Unexpected error loading organizations.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const handleCreated = () => {
    setCreating(false)
    void refresh()
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleOrganizations = useMemo(() => {
    if (trimmedSearch === '') return organizations
    return organizations.filter((organization) => organization.name.toLowerCase().includes(trimmedSearch))
  }, [organizations, trimmedSearch])

  if (creating) {
    return <OrganizationForm onCreated={handleCreated} onCancel={() => setCreating(false)} />
  }

  const columns: DataViewColumn<OrganizationSummary>[] = [
    { key: 'name', header: 'Name', cell: (organization) => organization.name },
    {
      key: 'createdAt',
      header: 'Created',
      cell: (organization) => formatCreatedAt(organization.createdAt),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title="Organizations"
        description="Tenants onboarded onto this platform, each with their own admin and branch."
        actions={<Button onClick={() => setCreating(true)}>New organization</Button>}
      />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel="Search organizations"
        searchPlaceholder="Search by name…"
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleOrganizations}
        columns={columns}
        getRowKey={(organization) => organization.id}
        view={view}
        loading={loading}
        emptyMessage={organizations.length === 0 ? 'No organizations yet.' : 'No organizations match this search.'}
        loadErrorMessage={loadError === null ? null : 'Organizations could not be loaded.'}
      />
    </section>
  )
}
