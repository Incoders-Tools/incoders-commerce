import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { CustomerForm } from './CustomerForm'
import { issueOrderingAccess, listCustomers } from '@/api/customers'
import { ApiError } from '@/api/client'
import type { CustomerRecord } from '@/api/types'

/**
 * List + create/edit (design.md "Two admin UIs against one endpoint set").
 * Reachable only through `RequireAdmin` (App.tsx), but the server's
 * `ManageUsers` check on every `/customers` call remains the real gate.
 */
export function CustomersScreen() {
  const [customers, setCustomers] = useState<CustomerRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editingCustomer, setEditingCustomer] = useState<CustomerRecord | null>(null)
  const [creating, setCreating] = useState(false)
  const [issuedCredential, setIssuedCredential] = useState<{ customerId: string; credential: string } | null>(null)

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

  if (creating || editingCustomer !== null) {
    return (
      <CustomerForm
        customer={editingCustomer ?? undefined}
        onSaved={handleSaved}
        onCancel={closeForm}
      />
    )
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-3xl">
      <CardHeader className="flex flex-row items-center justify-between">
        <CardTitle>Customers</CardTitle>
        <Button onClick={() => setCreating(true)}>New customer</Button>
      </CardHeader>
      <CardContent>
        {error && (
          <p role="alert" className="text-sm text-red-600">
            {error}
          </p>
        )}
        {issuedCredential && (
          <p data-testid="issued-credential" className="mb-4 text-sm text-neutral-700">
            Ordering access credential (shown once): {issuedCredential.credential}
          </p>
        )}
        {loading ? (
          <p>Loading…</p>
        ) : customers.length === 0 ? (
          <p>No customers yet.</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {customers.map((customer) => (
              <li
                key={customer.id}
                className="flex items-center justify-between border-b border-neutral-200 pb-2"
              >
                <div>
                  <p className="font-medium">{customer.displayName}</p>
                  <p className="text-xs text-neutral-500">
                    {customer.customerKind} · {customer.isEnabled ? 'Enabled' : 'Disabled'}
                  </p>
                </div>
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setEditingCustomer(customer)}>
                    Edit
                  </Button>
                  <Button variant="outline" size="sm" onClick={() => void handleIssueAccess(customer.id)}>
                    Issue ordering access
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}
