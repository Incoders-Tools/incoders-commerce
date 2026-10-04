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
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { changeProductCategory, listPresentations, listProducts, setProductActive, updatePresentation } from '@/api/catalog'
import { listCategories } from '@/api/categories'
import { ApiError } from '@/api/client'
import { hasPermission, useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { CatalogCopyForm } from './CatalogCopyForm'
import { Permission, QuantityBehavior, type CategoryRecord, type PresentationRecord, type ProductRecord } from '@/api/types'

const QUANTITY_BEHAVIOR_KEYS: Record<QuantityBehavior, 'fixedQuantity' | 'weighted' | 'bulk'> = {
  [QuantityBehavior.FixedQuantity]: 'fixedQuantity',
  [QuantityBehavior.Weighted]: 'weighted',
  [QuantityBehavior.Bulk]: 'bulk',
}

type StatusFilter = 'active' | 'inactive' | 'all'

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
  // Presentations are branch-owned: without a selected branch the API answers
  // 400 `branch-selection-required`, so the screen asks for one instead.
  const missingBranch = useMissingBranch()
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [error, setError] = useState<string | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('catalog')
  const [copying, setCopying] = useState(false)

  // Soft deletion: the default view asks the API for active products only (so nothing else is loaded); "inactive" and
  // "all" load everything plus the products, which say which ones are inactive.
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('active')
  const [inactiveProductIds, setInactiveProductIds] = useState<ReadonlySet<string>>(new Set())
  // operator-ux-adjustments T1: the list names the product ("Lengua"), not just its presentation ("Por kg").
  const [productNames, setProductNames] = useState<ReadonlyMap<string, string>>(new Map())
  const [deactivating, setDeactivating] = useState<PresentationRecord | null>(null)
  const [statusBusy, setStatusBusy] = useState(false)

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
    if (missingBranch) return
    let cancelled = false
    setLoading(true)
    setError(null)
    // In the default view the products only supply names, so failing to read them falls back to the presentation
    // name; "inactive"/"all" need them to know which products are inactive, so there they stay required.
    const load: Promise<[PresentationRecord[], ProductRecord[]]> =
      statusFilter === 'active'
        ? Promise.all([listPresentations(), listProducts().catch(() => [] as ProductRecord[])])
        : Promise.all([listPresentations(true), listProducts(true)])
    load
      .then(([items, products]) => {
        if (!cancelled) {
          setPresentations(items)
          setProductNames(new Map(products.map((product) => [product.id, product.name])))
          setInactiveProductIds(
            statusFilter === 'active'
              ? new Set<string>()
              : new Set(products.filter((product) => !product.isActive).map((product) => product.id)),
          )
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
  }, [t, missingBranch, statusFilter])

  const handleUpdated = (updated: PresentationRecord) => {
    setPresentations((current) => current.map((item) => (item.id === updated.id ? updated : item)))
    setEditingId(null)
  }

  const changeStatus = async (productId: string, active: boolean) => {
    setStatusBusy(true)
    setError(null)
    try {
      await setProductActive(productId, active)
      setInactiveProductIds((current) => {
        const next = new Set(current)
        if (active) next.delete(productId)
        else next.add(productId)
        return next
      })
      setDeactivating(null)
    } catch (err) {
      setDeactivating(null)
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedStatus'))
    } finally {
      setStatusBusy(false)
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visiblePresentations = useMemo(() => {
    const byStatus = presentations.filter((presentation) => {
      const inactive = inactiveProductIds.has(presentation.productId)
      return statusFilter === 'all' || (statusFilter === 'inactive' ? inactive : !inactive)
    })
    if (trimmedSearch === '') return byStatus
    return byStatus.filter(
      (presentation) =>
        (productNames.get(presentation.productId) ?? '').toLowerCase().includes(trimmedSearch) ||
        presentation.name.toLowerCase().includes(trimmedSearch) ||
        (presentation.identificationCode ?? '').toLowerCase().includes(trimmedSearch),
    )
  }, [presentations, productNames, inactiveProductIds, statusFilter, trimmedSearch])

  const displayName = (presentation: PresentationRecord) => productNames.get(presentation.productId) ?? presentation.name

  // Every hook above must run on every render, including while editing, so
  // this state-swap return sits after them (rules of hooks) — the same
  // ordering `CustomersScreen.tsx` uses for its own create/edit swap.
  const editingPresentation = presentations.find((presentation) => presentation.id === editingId) ?? null

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('title')} description={t('description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

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
    {
      key: 'name',
      header: t('columns.name'),
      cell: (presentation) => (
        <span className="inline-flex flex-wrap items-center gap-2">
          <span>{displayName(presentation)}</span>
          {displayName(presentation) !== presentation.name && (
            <span className="text-xs text-muted-foreground">{presentation.name}</span>
          )}
          {inactiveProductIds.has(presentation.productId) && (
            <span className="inline-flex rounded-full border border-border bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">
              {t('status.inactiveBadge')}
            </span>
          )}
        </span>
      ),
    },
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
      >
        <label htmlFor="catalog-status-filter" className="sr-only">
          {t('status.filterLabel')}
        </label>
        <Select
          id="catalog-status-filter"
          className="w-auto"
          value={statusFilter}
          onChange={(event) => setStatusFilter(event.target.value as StatusFilter)}
        >
          <option value="active">{t('status.active')}</option>
          <option value="inactive">{t('status.inactive')}</option>
          <option value="all">{t('status.all')}</option>
        </Select>
      </DataToolbar>

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
          <span className="inline-flex gap-2">
            <Button type="button" variant="outline" size="sm" onClick={() => setEditingId(presentation.id)}>
              {t('editCode')}
            </Button>
            {inactiveProductIds.has(presentation.productId) ? (
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={statusBusy}
                onClick={() => void changeStatus(presentation.productId, true)}
              >
                {t('status.reactivate')}
              </Button>
            ) : (
              <Button type="button" variant="outline" size="sm" onClick={() => setDeactivating(presentation)}>
                {t('status.deactivate')}
              </Button>
            )}
          </span>
        )}
      />

      {deactivating && (
        <ConfirmDialog
          title={t('status.dialog.title', { name: displayName(deactivating) })}
          message={t('status.dialog.message')}
          confirmLabel={t('status.dialog.confirm')}
          busyLabel={t('status.dialog.confirming')}
          busy={statusBusy}
          destructive
          onConfirm={() => void changeStatus(deactivating.productId, false)}
          onCancel={() => setDeactivating(null)}
        />
      )}
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
