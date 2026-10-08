import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { listPresentations, listProducts, setProductActive } from '@/api/catalog'
import { listCategories } from '@/api/categories'
import { ApiError } from '@/api/client'
import { hasPermission, useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { CatalogCopyForm } from './CatalogCopyForm'
import { CatalogProductForm, type CatalogFormTarget } from './CatalogProductForm'
import { Permission, QuantityBehavior, type CategoryRecord, type PresentationRecord, type ProductRecord } from '@/api/types'

const QUANTITY_BEHAVIOR_KEYS: Record<QuantityBehavior, 'fixedQuantity' | 'weighted' | 'bulk'> = {
  [QuantityBehavior.FixedQuantity]: 'fixedQuantity',
  [QuantityBehavior.Weighted]: 'weighted',
  [QuantityBehavior.Bulk]: 'bulk',
}

type StatusFilter = 'active' | 'inactive' | 'all'

export type CatalogSort = 'name-asc' | 'name-desc' | 'code' | 'category' | 'updated-desc'

const SORTS: CatalogSort[] = ['name-asc', 'name-desc', 'code', 'category', 'updated-desc']

function formatUpdatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString('es-AR')
}

const collator = new Intl.Collator('es', { sensitivity: 'base', numeric: true })

/**
 * The organization's catalog for the selected branch: one row per presentation, named by its product ("Lengua") with
 * the presentation as secondary text ("Por kg").
 *
 * Search, the category filter and the sorting are client-side over what `GET /catalog/presentations` (plus the products
 * and categories) returned. Products without a code sort last by code.
 *
 * Reading is open to every staff role (a seller looks products up to take orders); creating, editing, deactivating and
 * copying need `ManageCatalog`, and only then are those actions offered. The server enforces it either way.
 *
 * Create and edit replace the list with a full-screen form (`CatalogProductForm`); search, filters, sort and view live
 * here, so they survive the round trip.
 */
export function CatalogScreen() {
  const { t } = useTranslation('catalog')
  // Presentations are branch-owned: without a selected branch the API answers
  // 400 `branch-selection-required`, so the screen asks for one instead.
  const missingBranch = useMissingBranch()
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [products, setProducts] = useState<ReadonlyMap<string, ProductRecord>>(new Map())
  const [categories, setCategories] = useState<CategoryRecord[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [error, setError] = useState<string | null>(null)
  const [formTarget, setFormTarget] = useState<CatalogFormTarget | null>(null)
  const [search, setSearch] = useState('')
  const [categoryFilter, setCategoryFilter] = useState('')
  const [sort, setSort] = useState<CatalogSort>('name-asc')
  const [view, setView] = useViewPreference('catalog')
  const [copying, setCopying] = useState(false)

  // Soft deletion: the default view asks the API for active products only; "inactive" and "all" load everything plus
  // the products, which say which ones are inactive.
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('active')
  const [deactivating, setDeactivating] = useState<PresentationRecord | null>(null)
  const [statusBusy, setStatusBusy] = useState(false)

  const user = useOptionalAuth()?.user ?? null
  // Outside an AuthProvider (the screen rendered on its own) nobody is signed in to restrict: the server decides.
  const canManage = user === null || hasPermission(user, Permission.ManageCatalog) || user.isSystemAdmin

  // B7 U5b: copy to another branch of the same organization, offered when there is another branch to copy to.
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
    // In the default view the products only supply names and categories, so failing to read them falls back to the
    // presentation name; "inactive"/"all" need them to know which products are inactive, so there they stay required.
    // The categories only feed the filter and the form: without them both simply do without.
    const includeInactive = statusFilter !== 'active'
    const load: Promise<[PresentationRecord[], ProductRecord[], CategoryRecord[]]> = Promise.all([
      listPresentations(includeInactive),
      includeInactive ? listProducts(true) : listProducts().catch(() => [] as ProductRecord[]),
      listCategories().catch(() => [] as CategoryRecord[]),
    ])
    load
      .then(([items, loadedProducts, loadedCategories]) => {
        if (cancelled) return
        setPresentations(items)
        setProducts(new Map(loadedProducts.map((product) => [product.id, product])))
        setCategories(Array.isArray(loadedCategories) ? loadedCategories : [])
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

  const categoryNames = useMemo(() => new Map(categories.map((category) => [category.id, category.name])), [categories])
  const productOf = (presentation: PresentationRecord) => products.get(presentation.productId) ?? null
  const displayName = (presentation: PresentationRecord) => productOf(presentation)?.name ?? presentation.name
  const categoryNameOf = (presentation: PresentationRecord) => {
    const categoryId = productOf(presentation)?.categoryId
    return categoryId ? categoryNames.get(categoryId) ?? null : null
  }
  const isInactive = (presentation: PresentationRecord) => productOf(presentation)?.isActive === false

  const handleSaved = ({ presentation, product }: { presentation: PresentationRecord; product: ProductRecord | null }) => {
    setPresentations((current) =>
      current.some((item) => item.id === presentation.id)
        ? current.map((item) => (item.id === presentation.id ? presentation : item))
        : [...current, presentation],
    )
    if (product !== null) {
      setProducts((current) => new Map(current).set(product.id, product))
    }
    setFormTarget(null)
  }

  const changeStatus = async (productId: string, active: boolean) => {
    setStatusBusy(true)
    setError(null)
    try {
      const updated = (await setProductActive(productId, active)) as ProductRecord | undefined
      setProducts((current) => {
        const known = updated ?? current.get(productId)
        return known ? new Map(current).set(productId, { ...known, isActive: active }) : current
      })
      if (!active && statusFilter === 'active') {
        // The default view lists active products only: the deactivated one leaves it.
        setPresentations((current) => current.filter((presentation) => presentation.productId !== productId))
      }
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
    const filtered = presentations.filter((presentation) => {
      const product = products.get(presentation.productId)
      const inactive = product?.isActive === false
      if (statusFilter === 'active' && inactive) return false
      if (statusFilter === 'inactive' && !inactive) return false
      if (categoryFilter !== '' && product?.categoryId !== categoryFilter) return false
      if (trimmedSearch === '') return true
      return (
        (product?.name ?? '').toLowerCase().includes(trimmedSearch) ||
        presentation.name.toLowerCase().includes(trimmedSearch) ||
        (presentation.identificationCode ?? '').toLowerCase().includes(trimmedSearch)
      )
    })

    const nameOf = (presentation: PresentationRecord) => products.get(presentation.productId)?.name ?? presentation.name
    const categoryOf = (presentation: PresentationRecord) => {
      const categoryId = products.get(presentation.productId)?.categoryId
      return (categoryId ? categoryNames.get(categoryId) : undefined) ?? ''
    }
    const byName = (a: PresentationRecord, b: PresentationRecord) =>
      collator.compare(nameOf(a), nameOf(b)) || collator.compare(a.name, b.name)
    const compare: Record<CatalogSort, (a: PresentationRecord, b: PresentationRecord) => number> = {
      'name-asc': byName,
      'name-desc': (a, b) => byName(b, a),
      // Without a code last, so the coded ones read in order first.
      code: (a, b) =>
        (a.identificationCode === null ? 1 : 0) - (b.identificationCode === null ? 1 : 0) ||
        collator.compare(a.identificationCode ?? '', b.identificationCode ?? '') ||
        byName(a, b),
      category: (a, b) => collator.compare(categoryOf(a), categoryOf(b)) || byName(a, b),
      'updated-desc': (a, b) => b.updatedAtUtc.localeCompare(a.updatedAtUtc) || byName(a, b),
    }
    return [...filtered].sort(compare[sort])
  }, [presentations, products, categoryNames, statusFilter, categoryFilter, trimmedSearch, sort])

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

  if (formTarget !== null && canManage) {
    return (
      <CatalogProductForm
        target={formTarget}
        categories={categories}
        presentations={presentations}
        onCancel={() => setFormTarget(null)}
        onSaved={handleSaved}
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
          {isInactive(presentation) && (
            <span className="inline-flex rounded-full border border-border bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">
              {t('status.inactiveBadge')}
            </span>
          )}
        </span>
      ),
    },
    {
      key: 'category',
      header: t('columns.category'),
      cell: (presentation) => categoryNameOf(presentation) ?? <span className="text-muted-foreground">—</span>,
      hideOnMobile: true,
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
        description={canManage ? t('description') : t('readOnlyDescription')}
        actions={
          canManage || canCopy ? (
            <span className="inline-flex flex-wrap gap-2">
              {canCopy && (
                <Button type="button" variant="outline" onClick={() => setCopying(true)}>
                  {t('copy.action')}
                </Button>
              )}
              {canManage && (
                <Button type="button" onClick={() => setFormTarget({ mode: 'create' })}>
                  {t('productForm.newAction')}
                </Button>
              )}
            </span>
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
        {categories.length > 0 && (
          <>
            <label htmlFor="catalog-category-filter" className="sr-only">
              {t('filters.categoryLabel')}
            </label>
            <Select
              id="catalog-category-filter"
              className="w-auto"
              value={categoryFilter}
              onChange={(event) => setCategoryFilter(event.target.value)}
            >
              <option value="">{t('filters.allCategories')}</option>
              {categories.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
            </Select>
          </>
        )}
        {canManage && (
          <>
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
          </>
        )}
        <label htmlFor="catalog-sort" className="sr-only">
          {t('sort.label')}
        </label>
        <Select id="catalog-sort" className="w-auto" value={sort} onChange={(event) => setSort(event.target.value as CatalogSort)}>
          {SORTS.map((option) => (
            <option key={option} value={option}>
              {t(`sort.options.${option}`)}
            </option>
          ))}
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
        renderActions={
          canManage
            ? (presentation) => (
                <span className="inline-flex gap-2">
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={() => setFormTarget({ mode: 'edit', presentation, product: productOf(presentation) })}
                  >
                    {t('edit')}
                  </Button>
                  {isInactive(presentation) ? (
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
              )
            : undefined
        }
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
