import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { createBranch, listBranches } from '@/api/account'
import type { BranchSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'

/**
 * T4b: migrated onto the shared data-view layer (`components/data/*`),
 * following `CatalogScreen.tsx`, and reformatted — the pre-T4b file was a
 * single ~1 KB line.
 *
 * The create form is one field, so it lives in `PageHeader`'s action slot and
 * stays permanently visible: `e2e/admin-console.spec.ts` fills `Branch name`
 * straight after navigating here, and putting it behind a toggle would
 * silently break that real-backend journey.
 *
 * Search is a client-side filter over what `GET /account/branches` already
 * returned; there is no server-side branch search endpoint.
 */
export function BranchesScreen() {
  const [branches, setBranches] = useState<BranchSummary[]>([])
  const [name, setName] = useState('')
  /** Why the last load failed, if it did. Never set by an action: an action
   * failing says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('branches')

  const refresh = async () => {
    try {
      setBranches(await listBranches())
      setLoadError(null)
    } catch {
      setLoadError('Unable to load branches.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setActionError(null)
    try {
      await createBranch({ branchName: name })
      setName('')
      await refresh()
    } catch {
      setActionError('Unable to create branch.')
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleBranches = useMemo(() => {
    if (trimmedSearch === '') return branches
    return branches.filter((branch) => branch.branchName.toLowerCase().includes(trimmedSearch))
  }, [branches, trimmedSearch])

  const columns: DataViewColumn<BranchSummary>[] = [
    { key: 'branchName', header: 'Branch name', cell: (branch) => branch.branchName },
    {
      key: 'branchId',
      header: 'Identifier',
      cell: (branch) => <span className="font-mono text-xs text-muted-foreground">{branch.branchId}</span>,
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title="Branches"
        description="Physical locations orders and stock are attributed to."
        actions={
          <form onSubmit={submit} className="flex w-full items-center gap-2 sm:w-auto">
            <Input
              aria-label="Branch name"
              className="sm:w-56"
              placeholder="New branch name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
            />
            <Button type="submit">Create branch</Button>
          </form>
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
        searchLabel="Search branches"
        searchPlaceholder="Search by branch name…"
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleBranches}
        columns={columns}
        getRowKey={(branch) => branch.branchId}
        view={view}
        loading={loading}
        emptyMessage={branches.length === 0 ? 'No branches yet.' : 'No branches match this search.'}
        loadErrorMessage={loadError === null ? null : 'Branches could not be loaded.'}
      />
    </section>
  )
}
