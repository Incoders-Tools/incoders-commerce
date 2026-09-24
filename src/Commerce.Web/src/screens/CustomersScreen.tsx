import { useEffect, useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { CustomerForm } from './CustomerForm'
import { issueOrderingAccess, listCustomers } from '@/api/customers'
import { ApiError } from '@/api/client'
import type { CustomerRecord } from '@/api/types'

/**
 * List + create/edit (design.md "Two admin UIs against one endpoint set").
 * Reachable only through `RequireAdmin` (App.tsx), but the server's
 * `ManageUsers` check on every `/customers` call remains the real gate.
 *
 * T4b: migrated onto the shared data-view layer (`components/data/*`),
 * following `CatalogScreen.tsx`. The old `mx-auto … max-w-3xl` Card wrapper
 * is gone — the T3 shell already owns the page frame, so this screen now
 * fills the available width. Search is a client-side filter over what
 * `GET /customers` already returned; there is no server-side search endpoint.
 */
export function CustomersScreen() {
  const [customers, setCustomers] = useState<CustomerRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editingCustomer, setEditingCustomer] = useState<CustomerRecord | null>(null)
  const [creating, setCreating] = useState(false)
  const [issuedCredential, setIssuedCredential] = useState<{ customerId: string; credential: string } | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('customers')

  const refresh = async () => {
    setLoading(true)
    setError(null)
    try {
      setCustomers(await listCustomers())
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error loading customers.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
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
    setError(null)
    try {
      const result = await issueOrderingAccess(customerId)
      setIssuedCredential({ customerId, credential: result.credential })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error issuing ordering access.')
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleCustomers = useMemo(() => {
    if (trimmedSearch === '') return customers
    return customers.filter(
      (customer) =>
        customer.displayName.toLowerCase().includes(trimmedSearch) ||
        (customer.legalName ?? '').toLowerCase().includes(trimmedSearch) ||
        (customer.taxId ?? '').toLowerCase().includes(trimmedSearch) ||
        (customer.email ?? '').toLowerCase().includes(trimmedSearch),
    )
  }, [customers, trimmedSearch])

  // The create/edit form deliberately still replaces the whole screen, exactly
  // as before T4b — it is a long form, not an inline row edit.
  if (creating || editingCustomer !== null) {
    return (
      <CustomerForm
        customer={editingCustomer ?? undefined}
        onSaved={handleSaved}
        onCancel={closeForm}
      />
    )
  }

  const columns: DataViewColumn<CustomerRecord>[] = [
    { key: 'displayName', header: 'Name', cell: (customer) => customer.displayName },
    { key: 'customerKind', header: 'Kind', cell: (customer) => customer.customerKind },
    {
      key: 'isEnabled',
      header: 'Status',
      cell: (customer) => (customer.isEnabled ? 'Enabled' : 'Disabled'),
    },
    {
      key: 'taxId',
      header: 'Tax ID',
      cell: (customer) =>
        customer.taxId ?? <span className="text-muted-foreground">No tax ID</span>,
      hideOnMobile: true,
    },
    {
      key: 'phone',
      header: 'Phone',
      cell: (customer) => customer.phone ?? <span className="text-muted-foreground">—</span>,
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title="Customers"
        description="Accounts that can be sold to, and their ordering access."
        actions={<Button onClick={() => setCreating(true)}>New customer</Button>}
      />

      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      {issuedCredential && (
        <p
          data-testid="issued-credential"
          className="rounded-md border border-border bg-muted px-4 py-3 text-sm text-foreground"
        >
          Ordering access credential (shown once): {issuedCredential.credential}
        </p>
      )}

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel="Search customers"
        searchPlaceholder="Search by name, legal name or tax ID…"
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleCustomers}
        columns={columns}
        getRowKey={(customer) => customer.id}
        view={view}
        loading={loading}
        emptyMessage={
          error
            ? 'Customers could not be loaded.'
            : customers.length === 0
              ? 'No customers yet.'
              : 'No customers match this search.'
        }
        renderActions={(customer) => (
          <>
            <Button variant="outline" size="sm" onClick={() => setEditingCustomer(customer)}>
              Edit
            </Button>
            <Button variant="outline" size="sm" onClick={() => void handleIssueAccess(customer.id)}>
              Issue ordering access
            </Button>
          </>
        )}
      />
    </section>
  )
}
