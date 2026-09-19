import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { listPresentations, updatePresentation } from '@/api/catalog'
import { ApiError } from '@/api/client'
import type { PresentationRecord } from '@/api/types'

/**
 * design.md "Web: CatalogScreen rework": real presentation list +
 * identification-code editing, replacing the hand-typed rename form
 * (commerce-pricing-engine specs/catalog-item-identification/spec.md
 * "Requirement: Admin Editing of Identification Codes"). Uniqueness stays
 * enforced by the database's partial unique index, never by this UI.
 */
export function CatalogScreen() {
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    listPresentations()
      .then((items) => {
        if (!cancelled) {
          setPresentations(items)
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setError(err instanceof ApiError ? err.message : 'Unexpected error: the catalog service is unreachable.')
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  const handleUpdated = (updated: PresentationRecord) => {
    setPresentations((current) => current.map((item) => (item.id === updated.id ? updated : item)))
    setEditingId(null)
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-2xl">
      <CardHeader>
        <CardTitle>Catalog</CardTitle>
      </CardHeader>
      <CardContent>
        {error && (
          <p role="alert" className="text-sm text-red-600">
            {error}
          </p>
        )}

        {loading && <p>Loading…</p>}

        {!loading && presentations.length === 0 && !error && <p>No presentations yet.</p>}

        {!loading && presentations.length > 0 && (
          <ul className="flex flex-col gap-3">
            {presentations.map((presentation) => (
              <li key={presentation.id} className="border-b border-neutral-200 pb-3">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="font-medium">{presentation.name}</p>
                    <p className="text-xs text-neutral-500">{presentation.identificationCode ?? 'No code'}</p>
                  </div>
                  {editingId !== presentation.id && (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setEditingId(presentation.id)}
                    >
                      Edit code
                    </Button>
                  )}
                </div>
                {editingId === presentation.id && (
                  <IdentificationCodeForm
                    presentation={presentation}
                    onCancel={() => setEditingId(null)}
                    onUpdated={handleUpdated}
                  />
                )}
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

function IdentificationCodeForm({
  presentation,
  onCancel,
  onUpdated,
}: {
  presentation: PresentationRecord
  onCancel: () => void
  onUpdated: (updated: PresentationRecord) => void
}) {
  const [identificationCode, setIdentificationCode] = useState(presentation.identificationCode ?? '')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      const updated = await updatePresentation(presentation.id, {
        name: presentation.name,
        quantityBehavior: presentation.quantityBehavior,
        unitId: presentation.unitId,
        identificationCode: identificationCode.trim() === '' ? null : identificationCode.trim(),
      })
      onUpdated(updated)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error updating the identification code.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="mt-2 flex flex-wrap items-end gap-2" onSubmit={handleSubmit}>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor={`identificationCode-${presentation.id}`}>Identification code</Label>
        <Input
          id={`identificationCode-${presentation.id}`}
          value={identificationCode}
          onChange={(e) => setIdentificationCode(e.target.value)}
        />
      </div>
      {error && (
        <p role="alert" className="text-sm text-red-600">
          {error}
        </p>
      )}
      <Button type="submit" size="sm" disabled={submitting}>
        {submitting ? 'Saving…' : 'Save'}
      </Button>
      <Button type="button" variant="outline" size="sm" onClick={onCancel} disabled={submitting}>
        Cancel
      </Button>
    </form>
  )
}
