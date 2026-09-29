import { useEffect, useMemo, useState, type FormEvent } from 'react'
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
import { changeProductCategory, listPresentations, listProducts, updatePresentation } from '@/api/catalog'
import { listCategories } from '@/api/categories'
import { ApiError } from '@/api/client'
import { hasPermission, useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { CatalogCopyForm } from './CatalogCopyForm'
import { Permission, QuantityBehavior, type CategoryRecord, type PresentationRecord, type ProductRecord } from '@/api/types'

const QUANTITY_BEHAVIOR_KEYS: Record<QuantityBehavior, 'fixedQuantity' | 'weighted' | 'bulk'> = {
  [QuantityBehavior.FixedQuantity]: 'fixedQuantity',
  [QuantityBehavior.Weighted]: 'weighted',
  [QuantityBehavior.Bulk]: 'bulk',
}

function formatUpdatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString('es-AR')
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
 *
 * T9: "Edit code" used to open `IdentificationCodeForm` inline via
 * `DataView.renderExpanded` (a boxed row under the item) — the user's
 * complaint was that this read like an embedded modal. It now follows the
 * same full-screen state-swap `CustomersScreen`/`CustomerForm` established:
 * `editingId` swaps this component's own return value for `FormPage`
 * instead of expanding a row, and `search`/`view` stay intact across the
 * swap because they live in this same component, not a child that
 * unmounts.
 */
export function CatalogScreen() {
  const { t } = useTranslation('catalog')
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('catalog')
  const [copying, setCopying] = useState(false)

  // B7 U5b: admin-only copy to another branch of the same organization. The
  // server is the authority (`ManageCatalog`, same-organization targets); the
  // action is only offered when there is another branch to copy to.
  const user = useOptionalAuth()?.user ?? null
  const branchContext = useOptionalBranchContext()
  const sourceBranch = branchContext?.selectedBranch ?? null
  const targetBranches = (branchContext?.selectableBranches ?? []).filter((branch) => branch.id !== sourceBranch?.id)
  const canCopy =
    sourceBranch !== null &&
    targetBranches.length > 0 &&
    (hasPermission(user, Permission.ManageCatalog) || Boolean(user?.isSystemAdmin))

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
          setError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
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
  }, [t])

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

  // Every hook above must run on every render, including while editing, so
  // this state-swap return sits after them (rules of hooks) — the same
  // ordering `CustomersScreen.tsx` uses for its own create/edit swap.
  const editingPresentation = presentations.find((presentation) => presentation.id === editingId) ?? null

  if (copying && canCopy && sourceBranch) {
    return (
      <CatalogCopyForm sourceBranch={sourceBranch} targetBranches={targetBranches} onBack={() => setCopying(false)} />
    )
  }

  if (editingPresentation) {
    return (
      <IdentificationCodeForm
        presentation={editingPresentation}
        onCancel={() => setEditingId(null)}
        onUpdated={handleUpdated}
      />
    )
  }

  const columns: DataViewColumn<PresentationRecord>[] = [
    { key: 'name', header: t('columns.name'), cell: (presentation) => presentation.name },
    {
      key: 'identificationCode',
      header: t('columns.identificationCode'),
      cell: (presentation) =>
        presentation.identificationCode ?? <span className="text-muted-foreground">{t('columns.noCode')}</span>,
    },
    {
      key: 'quantityBehavior',
      header: t('columns.quantityBehavior'),
      cell: (presentation) =>
        t(`quantityBehaviorOptions.${QUANTITY_BEHAVIOR_KEYS[presentation.quantityBehavior] ?? 'unknown'}`),
      hideOnMobile: true,
    },
    {
      key: 'updatedAtUtc',
      header: t('columns.lastUpdated'),
      cell: (presentation) => formatUpdatedAt(presentation.updatedAtUtc),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          canCopy ? (
            <Button type="button" variant="outline" onClick={() => setCopying(true)}>
              {t('copy.action')}
            </Button>
          ) : undefined
        }
      />

      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
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
        items={visiblePresentations}
        columns={columns}
        getRowKey={(presentation) => presentation.id}
        view={view}
        loading={loading}
        emptyMessage={
          error
            ? t('empty.loadError')
            : presentations.length === 0
              ? t('empty.none')
              : t('empty.noMatch')
        }
        renderActions={(presentation) => (
          <Button type="button" variant="outline" size="sm" onClick={() => setEditingId(presentation.id)}>
            {t('editCode')}
          </Button>
        )}
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
  const { t } = useTranslation('catalog')
  const [identificationCode, setIdentificationCode] = useState(presentation.identificationCode ?? '')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  // catalog-categories: the presentation's PRODUCT carries the category. It is
  // loaded alongside the form and is strictly optional — when the products or
  // categories cannot be read the select is simply not offered, and the
  // identification code can still be saved (that edit predates categories).
  const [categories, setCategories] = useState<CategoryRecord[]>([])
  const [product, setProduct] = useState<ProductRecord | null>(null)
  const [categoryId, setCategoryId] = useState('')

  useEffect(() => {
    let cancelled = false
    Promise.all([listProducts(), listCategories()])
      .then(([products, loadedCategories]) => {
        if (cancelled) return
        const owner = products.find((item) => item.id === presentation.productId) ?? null
        setProduct(owner)
        setCategories(loadedCategories)
        setCategoryId(owner?.categoryId ?? '')
      })
      .catch(() => {
        // Category editing is unavailable; the code form keeps working.
      })
    return () => {
      cancelled = true
    }
  }, [presentation.productId])

  const canEditCategory = product !== null && categories.some((category) => category.id === categoryId)

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
      if (product !== null && canEditCategory && categoryId !== product.categoryId) {
        await changeProductCategory(product.id, categoryId)
      }
      onUpdated(updated)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedUpdate'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={t('form.title')}
      description={t('form.description', { name: presentation.name })}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex max-w-md flex-col gap-4" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="identificationCode">{t('form.label')}</Label>
          <Input
            id="identificationCode"
            value={identificationCode}
            onChange={(e) => setIdentificationCode(e.target.value)}
          />
        </div>
        {canEditCategory && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="productCategory">{t('form.category')}</Label>
            <Select id="productCategory" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              {categories.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
            </Select>
          </div>
        )}
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
