import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { listOrganizations } from '@/api/account'
import { ApiError } from '@/api/client'
import type { OrganizationSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { useOrganizationContext } from '@/organization/OrganizationContext'
import { OrganizationForm } from './OrganizationForm'
import { OrganizationBrandingForm } from './OrganizationBrandingForm'

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
 * state-swap. Reachable only through `RequireSystemAdmin` (App.tsx).
 *
 * T5b: added an "Edit branding" row action opening `OrganizationBrandingForm`
 * (logoUrl + primaryColor only — date format, geolocation and usage plan
 * stay deferred per the user's minimal-scope decision). The list itself is
 * unchanged: branding isn't a column here, it's fetched by the form when it
 * opens (`GET /account/organizations/{id}/branding`).
 */
export function OrganizationsScreen() {
  const navigate = useNavigate()
  const { selectOrganization } = useOrganizationContext()
  const [organizations, setOrganizations] = useState<OrganizationSummary[]>([])
  const [loading, setLoading] = useState(true)
  /** Why the last load failed, if it did. Never set by the create form: a
   * failed create says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [editingBranding, setEditingBranding] = useState<OrganizationSummary | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('organizations')
  // Guards against a slow refresh resolving after a newer one: `refresh` can
  // run more than once (mount, then again after a create), and a fetch has
  // no cancellation of its own, so a stale response landing after a fresh
  // one could otherwise overwrite it with older data. Each call claims the
  // next sequence number and only applies its result if it is still the
  // most recent call in flight when it resolves.
  const refreshSequence = useRef(0)

  const refresh = async () => {
    const sequence = ++refreshSequence.current
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listOrganizations()
      if (sequence === refreshSequence.current) {
        setOrganizations(result)
      }
    } catch (err) {
      if (sequence === refreshSequence.current) {
        setLoadError(err instanceof ApiError ? err.message : 'Unexpected error loading organizations.')
      }
    } finally {
      if (sequence === refreshSequence.current) {
        setLoading(false)
      }
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const handleCreated = () => {
    setCreating(false)
    void refresh()
  }

  // platform-administration spec, "Sysadmin Acts On A Selected
  // Organization": selects this organization and jumps straight to its
  // Branches screen — the same screen a business-admin of that organization
  // would use, reused rather than duplicated under Organizations.
  const handleOpen = (organization: OrganizationSummary) => {
    selectOrganization({ id: organization.id, name: organization.name })
    navigate('/app/branches')
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleOrganizations = useMemo(() => {
    if (trimmedSearch === '') return organizations
    return organizations.filter((organization) => organization.name.toLowerCase().includes(trimmedSearch))
  }, [organizations, trimmedSearch])

  if (creating) {
    return <OrganizationForm onCreated={handleCreated} onCancel={() => setCreating(false)} />
  }

  if (editingBranding !== null) {
    return (
      <OrganizationBrandingForm
        organization={editingBranding}
        onSaved={() => setEditingBranding(null)}
        onCancel={() => setEditingBranding(null)}
      />
    )
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
        renderActions={(organization) => (
          <>
            <Button variant="outline" size="sm" onClick={() => handleOpen(organization)}>
              Open
            </Button>
            <Button variant="outline" size="sm" onClick={() => setEditingBranding(organization)}>
              Edit branding
            </Button>
          </>
        )}
      />
    </section>
  )
}
