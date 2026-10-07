import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { FormPage } from '@/components/layout/FormPage'
import { createCategory, deleteCategory, listCategories, updateCategory } from '@/api/categories'
import { ApiError } from '@/api/client'
import type { CategoryRecord } from '@/api/types'
import {
  CATEGORY_ICON_KEYS,
  DEFAULT_CATEGORY_ICON_KEY,
  categoryIcon,
  isCategoryIconKey,
} from '@/lib/categoryIcons'
import { cn } from '@/lib/utils'

/**
 * catalog-categories spec: admins manage the organization's product
 * categories — a name and an icon key from the fixed set the POS rail uses.
 * Follows `CatalogScreen`'s shape (PageHeader + DataToolbar + DataView, with a
 * full-screen `FormPage` state-swap for create/edit). The server is the
 * authority for permissions, per-organization name uniqueness and refusing to
 * delete a category products still use; this screen only reports those
 * outcomes in Spanish.
 */
export function CategoriesScreen() {
  const { t } = useTranslation('categories')
  const [categories, setCategories] = useState<CategoryRecord[]>([])
  const [loading, setLoading] = useState(true)
  /** Why the last load failed, if it did. Never set by an action. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('categories')
  const [editing, setEditing] = useState<CategoryRecord | 'new' | null>(null)
  const [confirmingDeleteId, setConfirmingDeleteId] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setCategories(await listCategories())
      setLoadError(null)
    } catch {
      setLoadError(t('errors.unableToLoad'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const handleDelete = async (category: CategoryRecord) => {
    setActionError(null)
    try {
      await deleteCategory(category.id)
      setConfirmingDeleteId(null)
      await refresh()
    } catch (err) {
      setConfirmingDeleteId(null)
      setActionError(
        err instanceof ApiError && err.status === 409 && err.message.includes('category-in-use')
          ? t('errors.inUse')
          : t('errors.unableToDelete'),
      )
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleCategories = useMemo(() => {
    if (trimmedSearch === '') return categories
    return categories.filter((category) => category.name.toLowerCase().includes(trimmedSearch))
  }, [categories, trimmedSearch])

  if (editing !== null) {
    return (
      <CategoryForm
        category={editing === 'new' ? null : editing}
        onCancel={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null)
          await refresh()
        }}
      />
    )
  }

  const columns: DataViewColumn<CategoryRecord>[] = [
    {
      key: 'name',
      header: t('columns.name'),
      cell: (category) => {
        const Icon = categoryIcon(category.iconKey)
        return (
          <span className="inline-flex items-center gap-2">
            <Icon aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
            <span>{category.name}</span>
          </span>
        )
      },
    },
    {
      key: 'icon',
      header: t('columns.icon'),
      cell: (category) => (isCategoryIconKey(category.iconKey) ? t(`icons.${category.iconKey}`) : category.iconKey),
      hideOnMobile: true,
    },
    {
      key: 'pos',
      header: t('columns.pos'),
      cell: (category) =>
        category.showInPos === false
          ? t('pos.hidden')
          : t('pos.shown', { order: category.posSortOrder ?? 0 }),
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          <Button type="button" onClick={() => setEditing('new')}>
            {t('actions.create')}
          </Button>
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
        searchLabel={t('search.label')}
        searchPlaceholder={t('search.placeholder')}
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleCategories}
        columns={columns}
        getRowKey={(category) => category.id}
        view={view}
        loading={loading}
        emptyMessage={categories.length === 0 ? t('empty.none') : t('empty.noMatch')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(category) =>
          confirmingDeleteId === category.id ? (
            <>
              <Button type="button" variant="destructive" size="sm" onClick={() => void handleDelete(category)}>
                {t('actions.confirmDelete')}
              </Button>
              <Button type="button" variant="outline" size="sm" onClick={() => setConfirmingDeleteId(null)}>
                {t('actions.cancel')}
              </Button>
            </>
          ) : (
            <>
              <Button type="button" variant="outline" size="sm" onClick={() => setEditing(category)}>
                {t('actions.edit')}
              </Button>
              <Button type="button" variant="outline" size="sm" onClick={() => setConfirmingDeleteId(category.id)}>
                {t('actions.delete')}
              </Button>
            </>
          )
        }
      />
    </section>
  )
}

function CategoryForm({
  category,
  onCancel,
  onSaved,
}: {
  category: CategoryRecord | null
  onCancel: () => void
  onSaved: () => Promise<void>
}) {
  const { t } = useTranslation('categories')
  const [name, setName] = useState(category?.name ?? '')
  const [iconKey, setIconKey] = useState<string>(
    category && isCategoryIconKey(category.iconKey) ? category.iconKey : DEFAULT_CATEGORY_ICON_KEY,
  )
  const [showInPos, setShowInPos] = useState(category?.showInPos ?? true)
  const [posSortOrder, setPosSortOrder] = useState(String(category?.posSortOrder ?? 0))
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    const order = Number(posSortOrder.trim() === '' ? '0' : posSortOrder)
    if (!Number.isInteger(order) || order < 0 || order > 9999) {
      setError(t('errors.posSortOrder'))
      return
    }
    setSubmitting(true)
    try {
      const request = { name: name.trim(), iconKey, showInPos, posSortOrder: order }
      if (category) {
        await updateCategory(category.id, request)
      } else {
        await createCategory(request)
      }
      await onSaved()
    } catch (err) {
      setError(
        err instanceof ApiError && err.status === 409 && err.message.includes('category-name-in-use')
          ? t('errors.nameInUse')
          : t('errors.unableToSave'),
      )
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={category ? t('form.editTitle') : t('form.createTitle')}
      description={t('form.description')}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex max-w-xl flex-col gap-5" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="categoryName">{t('form.name')}</Label>
          <Input id="categoryName" value={name} onChange={(e) => setName(e.target.value)} required />
        </div>

        <div role="radiogroup" aria-label={t('form.icon')} className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          {CATEGORY_ICON_KEYS.map((key) => {
            const Icon = categoryIcon(key)
            const selected = iconKey === key
            return (
              <label
                key={key}
                className={cn(
                  'flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm transition-colors focus-within:ring-1 focus-within:ring-ring',
                  selected ? 'border-primary bg-primary/10 text-foreground' : 'border-input hover:bg-accent',
                )}
              >
                <input
                  type="radio"
                  name="categoryIcon"
                  value={key}
                  checked={selected}
                  onChange={() => setIconKey(key)}
                  className="sr-only"
                />
                <Icon aria-hidden="true" className="size-4 shrink-0" />
                <span>{t(`icons.${key}`)}</span>
              </label>
            )
          })}
        </div>

        <fieldset className="flex flex-col gap-3 rounded-md border border-border p-4">
          <legend className="px-1 text-sm font-medium">{t('form.pos.title')}</legend>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={showInPos} onChange={(e) => setShowInPos(e.target.checked)} />
            {t('form.pos.show')}
          </label>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="categoryPosSortOrder">{t('form.pos.order')}</Label>
            <Input
              id="categoryPosSortOrder"
              type="number"
              min={0}
              max={9999}
              step={1}
              className="w-28"
              value={posSortOrder}
              disabled={!showInPos}
              onChange={(e) => setPosSortOrder(e.target.value)}
            />
          </div>
          <p className="text-xs text-muted-foreground">{t('form.pos.hint')}</p>
        </fieldset>

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
