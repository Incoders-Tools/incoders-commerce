import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { renameProduct } from '@/api/catalog'
import { ApiError } from '@/api/client'
import type { ManagementOutcome } from '@/api/types'

const MANAGE_CATALOG_PERMISSION = 1 << 1

export function CatalogScreen() {
  const [productId, setProductId] = useState('')
  const [actorId, setActorId] = useState('')
  const [targetBranchId, setTargetBranchId] = useState('')
  const [currentName, setCurrentName] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [defaultUnitId, setDefaultUnitId] = useState('')
  const [newName, setNewName] = useState('')
  const [outcome, setOutcome] = useState<ManagementOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setOutcome(null)
    setSubmitting(true)
    try {
      const result = await renameProduct(productId, {
        actorId,
        actorBranchScope: [targetBranchId],
        actorRoles: [{ name: 'catalog-manager', permissions: MANAGE_CATALOG_PERMISSION }],
        targetBranchId,
        currentName,
        categoryId,
        defaultUnitId,
        newName,
        isOffline: false,
        correlationId: crypto.randomUUID(),
      })
      setOutcome(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error renaming product.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-lg">
      <CardHeader>
        <CardTitle>Rename product</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <Field id="productId" label="Product ID" value={productId} onChange={setProductId} />
          <Field id="actorId" label="Actor ID" value={actorId} onChange={setActorId} />
          <Field id="targetBranchId" label="Target branch ID" value={targetBranchId} onChange={setTargetBranchId} />
          <Field id="currentName" label="Current name" value={currentName} onChange={setCurrentName} />
          <Field id="categoryId" label="Category ID" value={categoryId} onChange={setCategoryId} />
          <Field id="defaultUnitId" label="Default unit ID" value={defaultUnitId} onChange={setDefaultUnitId} />
          <Field id="newName" label="New name" value={newName} onChange={setNewName} />

          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}
          {outcome && (
            <p data-testid="catalog-outcome" className="text-sm text-neutral-700">
              {outcome.status === 'Allowed'
                ? `Renamed to "${outcome.updatedProduct?.name}".`
                : `Denied: ${outcome.reason}`}
            </p>
          )}

          <Button type="submit" disabled={submitting}>
            {submitting ? 'Renaming…' : 'Rename product'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} value={value} onChange={(e) => onChange(e.target.value)} required />
    </div>
  )
}
