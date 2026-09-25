import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { listPresentations, updatePresentation } from '@/api/catalog'
import { ApiError } from '@/api/client'
import { QuantityBehavior, type PresentationRecord } from '@/api/types'

const QUANTITY_BEHAVIOR_LABELS: Record<QuantityBehavior, string> = {
  [QuantityBehavior.FixedQuantity]: 'Fixed quantity',
  [QuantityBehavior.Weighted]: 'Weighted',
  [QuantityBehavior.Bulk]: 'Bulk',
}

function formatUpdatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString()
}

/**
 * design.md "Web: CatalogScreen rework": real presentation list +
 * identification-code editing, replacing the hand-typed rename form
 * (commerce-pricing-engine specs/catalog-item-identification/spec.md
 * "Requirement: Admin Editing of Identification Codes"). Uniqueness stays
 * enforced by the database's partial unique index, never by this UI.
 *
 * T4 reference implementation of the shared data-view layer
 * (`components/data/*`): full-width PageHeader + DataToolbar + DataView,
 * no centered `max-w-*` card — the T3 shell already owns the page frame.
 * Search is a client-side filter over what `GET /catalog/presentations`
 * already returned; there is no server-side search endpoint.
 */
export function CatalogScreen() {
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('catalog')

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

  const trimmedSearch = search.trim().toLowerCase()
  const visiblePresentations = useMemo(() => {
    if (trimmedSearch === '') return presentations
    return presentations.filter(
      (presentation) =>
        presentation.name.toLowerCase().includes(trimmedSearch) ||
        (presentation.identificationCode ?? '').toLowerCase().includes(trimmedSearch),
    )
  }, [presentations, trimmedSearch])

  const columns: DataViewColumn<PresentationRecord>[] = [
    { key: 'name', header: 'Name', cell: (presentation) => presentation.name },
    {
      key: 'identificationCode',
      header: 'Identification code',
      cell: (presentation) =>
        presentation.identificationCode ?? <span className="text-muted-foreground">No code</span>,
    },
    {
      key: 'quantityBehavior',
      header: 'Quantity behavior',
      cell: (presentation) => QUANTITY_BEHAVIOR_LABELS[presentation.quantityBehavior] ?? 'Unknown',
      hideOnMobile: true,
    },
    {
      key: 'updatedAtUtc',
      header: 'Last updated',
      cell: (presentation) => formatUpdatedAt(presentation.updatedAtUtc),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader title="Catalog" description="Presentations available to sell, and their identification codes." />

      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel="Search presentations"
        searchPlaceholder="Search by name or code…"
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visiblePresentations}
        columns={columns}
        getRowKey={(presentation) => presentation.id}
        view={view}
        loading={loading}
        emptyMessage={
          error
            ? 'The catalog could not be loaded.'
            : presentations.length === 0
              ? 'No presentations yet.'
              : 'No presentations match this search.'
        }
        renderActions={(presentation) =>
          editingId === presentation.id ? null : (
            <Button type="button" variant="outline" size="sm" onClick={() => setEditingId(presentation.id)}>
              Edit code
            </Button>
          )
        }
        renderExpanded={(presentation) =>
          editingId === presentation.id ? (
            <IdentificationCodeForm
              presentation={presentation}
              onCancel={() => setEditingId(null)}
              onUpdated={handleUpdated}
            />
          ) : null
        }
      />
    </section>
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
    <form className="flex flex-wrap items-end gap-2" onSubmit={handleSubmit}>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor={`identificationCode-${presentation.id}`}>Identification code</Label>
        <Input
          id={`identificationCode-${presentation.id}`}
          value={identificationCode}
          onChange={(e) => setIdentificationCode(e.target.value)}
        />
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
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
