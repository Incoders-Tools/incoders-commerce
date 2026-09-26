import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { CustomerForm } from './CustomerForm'
import { issueOrderingAccess, listCustomers } from '@/api/customers'
import { ApiError } from '@/api/client'
import { CustomerKind, type CustomerRecord } from '@/api/types'

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
  const [view, setView] = useViewPreference('customers')

  const refresh = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setCustomers(await listCustomers())
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void refresh()
  }, [refresh])

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
    { key: 'displayName', header: t('columns.name'), cell: (customer) => customer.displayName },
    {
      key: 'customerKind',
      header: t('columns.kind'),
      cell: (customer) => t(`kindOptions.${customer.customerKind === CustomerKind.Wholesale ? 'wholesale' : 'retail'}`),
    },
    {
      key: 'isEnabled',
      header: t('columns.status'),
      cell: (customer) => (customer.isEnabled ? t('statusOptions.enabled') : t('statusOptions.disabled')),
    },
    {
      key: 'taxId',
      header: t('columns.taxId'),
      cell: (customer) =>
        customer.taxId ?? <span className="text-muted-foreground">{t('columns.noTaxId')}</span>,
      hideOnMobile: true,
    },
    {
      key: 'phone',
      header: t('columns.phone'),
      cell: (customer) => customer.phone ?? <span className="text-muted-foreground">{t('columns.noPhone')}</span>,
      hideOnMobile: true,
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
      />

      <DataView
        items={visibleCustomers}
        columns={columns}
        getRowKey={(customer) => customer.id}
        view={view}
        loading={loading}
        emptyMessage={customers.length === 0 ? t('empty.none') : t('empty.noMatch')}
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
