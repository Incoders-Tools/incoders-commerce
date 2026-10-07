import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { useUnsavedChanges } from '@/components/layout/UnsavedChanges'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { parseAmount } from '@/lib/quantity'
import { applyPercent, composeFinal, differencePercent, hasAtMostTwoDecimals, round2 } from '@/lib/priceEditing'
import { useNumberFormat } from '@/organization/NumberFormatContext'
import { ApiError } from '@/api/client'
import { listProducts } from '@/api/catalog'
import { listCategories } from '@/api/categories'
import { floorViolationsOf, getBreakdown, getComposition, publishEntriesBatch } from '@/api/pricing'
import type {
  BreakdownItem,
  CategoryRecord,
  FloorViolation,
  PresentationRecord,
  PriceListRecord,
  ProductRecord,
  RateComponent,
} from '@/api/types'
import { PriceHistory } from './PriceHistory'
import { summarizeComponents } from './compositionSummary'

interface PriceEditorTabProps {
  priceLists: PriceListRecord[]
  presentations: PresentationRecord[]
  /** The list being edited; the screen owns it so "Gestionar precios" can preselect one. */
  priceListId: string | null
  onPriceListChange: (priceListId: string) => void
  /** How many rows hold an unpublished edit, so the screen can ask before leaving the tab. */
  onDirtyChange?: (count: number) => void
  /** Opens the composition of the list (IVA, IB, flete, remarcación) to change it. */
  onEditComposition?: (priceListId: string) => void
}

/** One presentation of the edited list, as the grid shows it. */
interface EditorRow {
  presentationId: string
  productName: string
  presentationName: string
  /** The presentation name only adds information when its product has more than one. */
  showPresentation: boolean
  /** How the row is named to assistive technology and in the history panel. */
  label: string
  code: string | null
  categoryId: string | null
  categoryName: string | null
  base: number | null
  final: number | null
}

type RowEdit = { status: 'unchanged' } | { status: 'invalid' } | { status: 'changed'; value: number }

/** Which rows the grid shows by their price in the list: all, only the priced ones, or only those still without a price. */
type PriceFilter = 'all' | 'priced' | 'missing'

const normalize = (text: string) =>
  text
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()

/** What the remark writes into "Nueva base": cents with a decimal comma, readable back by `parseAmount`. */
const toInputText = (value: number) => value.toFixed(2).replace('.', ',')

/**
 * "Editar precios": the base prices of one list, edited one by one or remarked by a percentage, and
 * published together through ONE all-or-nothing batch. The composition (IVA, IB, flete, remarcación)
 * keeps applying on top, so the grid shows the current base and final side by side.
 */
export function PriceEditorTab({
  priceLists,
  presentations,
  priceListId,
  onPriceListChange,
  onDirtyChange,
  onEditComposition,
}: PriceEditorTabProps) {
  const { t } = useTranslation('priceLists')
  const [products, setProducts] = useState<ProductRecord[]>([])
  const [categories, setCategories] = useState<CategoryRecord[]>([])
  const [catalogError, setCatalogError] = useState<string | null>(null)
  const [dirtyCount, setDirtyCount] = useState(0)
  // The list chosen while edits were pending, waiting for "Descartar".
  const [pendingListId, setPendingListId] = useState<string | null>(null)

  useEffect(() => {
    onDirtyChange?.(dirtyCount)
  }, [dirtyCount, onDirtyChange])

  // Leaving through the app's links (sidebar, account menu) asks with the same message as the tab and list.
  useUnsavedChanges(dirtyCount > 0 ? t('editor.discard.message', { count: dirtyCount }) : null)

  // Closing or reloading the browser tab would lose the edits: the browser asks while any is pending.
  useEffect(() => {
    if (dirtyCount === 0) return
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirtyCount])

  const changeList = (nextId: string) => {
    if (dirtyCount > 0) setPendingListId(nextId)
    else onPriceListChange(nextId)
  }

  useEffect(() => {
    let cancelled = false
    Promise.all([listProducts(), listCategories()])
      .then(([loadedProducts, loadedCategories]) => {
        if (cancelled) return
        setProducts(loadedProducts)
        setCategories(loadedCategories)
      })
      .catch((err) => {
        if (!cancelled) setCatalogError(err instanceof ApiError ? err.message : t('editor.errors.unexpectedLoad'))
      })
    return () => {
      cancelled = true
    }
  }, [t])

  const priceList = priceLists.find((list) => list.id === priceListId) ?? null

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5 sm:max-w-xs">
        <Label htmlFor="price-editor-list">{t('editor.listLabel')}</Label>
        <Select id="price-editor-list" value={priceListId ?? ''} onChange={(e) => changeList(e.target.value)}>
          {priceListId === null && <option value="">{t('editor.noList')}</option>}
          {priceLists.map((list) => (
            <option key={list.id} value={list.id}>
              {list.name}
            </option>
          ))}
        </Select>
      </div>

      {catalogError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {catalogError}
        </p>
      )}

      {priceList && (
        // Another list is a fresh editor: its own prices, edits and selection.
        <PriceEditorGrid
          key={priceList.id}
          priceList={priceList}
          presentations={presentations}
          products={products}
          categories={categories}
          onDirtyChange={setDirtyCount}
          onEditComposition={onEditComposition ? () => onEditComposition(priceList.id) : undefined}
        />
      )}

      {pendingListId !== null && (
        <DiscardEditsDialog
          count={dirtyCount}
          onDiscard={() => {
            onPriceListChange(pendingListId)
            setPendingListId(null)
          }}
          onKeepEditing={() => setPendingListId(null)}
        />
      )}
    </div>
  )
}

/** "Tenés N cambios sin publicar. ¿Descartarlos?": asked before anything drops the pending edits. */
export function DiscardEditsDialog({
  count,
  onDiscard,
  onKeepEditing,
}: {
  count: number
  onDiscard: () => void
  onKeepEditing: () => void
}) {
  const { t } = useTranslation('priceLists')
  return (
    <ConfirmDialog
      title={t('editor.discard.title')}
      message={t('editor.discard.message', { count })}
      confirmLabel={t('editor.discard.confirm')}
      busyLabel={t('editor.discard.confirm')}
      cancelLabel={t('editor.discard.keepEditing')}
      destructive
      onConfirm={onDiscard}
      onCancel={onKeepEditing}
    />
  )
}

function PriceEditorGrid({
  priceList,
  presentations,
  products,
  categories,
  onDirtyChange,
  onEditComposition,
}: {
  priceList: PriceListRecord
  presentations: PresentationRecord[]
  products: ProductRecord[]
  categories: CategoryRecord[]
  onDirtyChange: (count: number) => void
  onEditComposition?: () => void
}) {
  const { t } = useTranslation('priceLists')
  const { t: tCommon } = useTranslation('common')
  const numberFormat = useNumberFormat()
  const [items, setItems] = useState<BreakdownItem[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [reloadToken, setReloadToken] = useState(0)
  // Empty means "today" as the server's business day decides it, not the browser's calendar.
  const [effectiveFrom, setEffectiveFrom] = useState('')
  // The server's business day, as the breakdown without a date reports it: the earliest date to publish on.
  const [businessDay, setBusinessDay] = useState<string | null>(null)
  const [dateError, setDateError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [categoryId, setCategoryId] = useState('')
  const [priceFilter, setPriceFilter] = useState<PriceFilter>('all')
  // The composition the published bases will get on the chosen date: it turns a new base into its new final price.
  const [components, setComponents] = useState<RateComponent[] | null>(null)
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [selected, setSelected] = useState<Set<string>>(() => new Set())
  const [percentText, setPercentText] = useState('')
  const [percentError, setPercentError] = useState<string | null>(null)
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({})
  const [publishError, setPublishError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [publishing, setPublishing] = useState(false)
  const [historyRow, setHistoryRow] = useState<EditorRow | null>(null)
  const gridRef = useRef<HTMLDivElement>(null)

  // The prices the edits replace: the breakdown of the picked date, or of today (the server's business
  // day) with none, so a date that already has an entry compares against that entry.
  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    getBreakdown(priceList.id, effectiveFrom === '' ? undefined : effectiveFrom)
      .then((breakdown) => {
        if (cancelled) return
        setItems(breakdown.items)
        if (effectiveFrom === '') setBusinessDay(breakdown.on)
      })
      .catch((err) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : t('editor.errors.unexpectedLoad'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [priceList.id, effectiveFrom, reloadToken, t])

  useEffect(() => {
    let cancelled = false
    setComponents(null)
    getComposition(priceList.id, effectiveFrom === '' ? undefined : effectiveFrom).then(
      (composition) => {
        if (!cancelled) setComponents(composition.components)
      },
      () => {
        if (!cancelled) setComponents(null)
      },
    )
    return () => {
      cancelled = true
    }
  }, [priceList.id, effectiveFrom, reloadToken])

  const rows = useMemo(
    () => buildRows(presentations, products, categories, items),
    [presentations, products, categories, items],
  )

  const visibleRows = useMemo(() => {
    const term = normalize(search.trim())
    return rows.filter((row) => {
      if (categoryId !== '' && row.categoryId !== categoryId) return false
      if (priceFilter === 'missing' && row.base !== null) return false
      if (priceFilter === 'priced' && row.base === null) return false
      if (term === '') return true
      return [row.productName, row.presentationName, row.code ?? ''].some((value) => normalize(value).includes(term))
    })
  }, [rows, search, categoryId, priceFilter])

  const editOf = (row: EditorRow): RowEdit => {
    const text = edits[row.presentationId]?.trim() ?? ''
    if (text === '') return { status: 'unchanged' }
    const value = parseAmount(text)
    if (value === null || value <= 0 || !hasAtMostTwoDecimals(value)) return { status: 'invalid' }
    if (row.base !== null && round2(value) === row.base) return { status: 'unchanged' }
    return { status: 'changed', value: round2(value) }
  }

  const rowEdits = rows.map((row) => ({ row, edit: editOf(row) }))
  const changed = rowEdits.filter((entry) => entry.edit.status === 'changed')
  const invalid = rowEdits.filter((entry) => entry.edit.status === 'invalid')
  // The prices on screen are those of another date (a date just picked, or its load failed): nothing
  // may compare against, remark or publish over them until the right ones arrive.
  const baselineStale = loading || loadError !== null
  const canPublish = changed.length > 0 && invalid.length === 0 && !publishing && !baselineStale
  // An invalid value is unpublished work too.
  const dirtyCount = rowEdits.filter((entry) => entry.edit.status !== 'unchanged').length

  useEffect(() => {
    onDirtyChange(dirtyCount)
  }, [dirtyCount, onDirtyChange])

  // Another list (or leaving the tab) unmounts the grid: nothing of it stays pending.
  useEffect(() => () => onDirtyChange(0), [onDirtyChange])

  const allVisibleSelected = visibleRows.length > 0 && visibleRows.every((row) => selected.has(row.presentationId))

  // The remark applies to the selected rows among those shown or, with none selected, to every row the filters show:
  // never to rows the filters hide. Rows without a base have nothing to remark.
  const selectedVisible = visibleRows.filter((row) => selected.has(row.presentationId))
  const remarkScope = selectedVisible.length > 0 ? 'selected' : visibleRows.length < rows.length ? 'filtered' : 'all'
  const remarkTargets = (selectedVisible.length > 0 ? selectedVisible : visibleRows).filter((row) => row.base !== null)
  const missingCount = rows.filter((row) => row.base === null).length

  const setEdit = (presentationId: string, text: string) => {
    setEdits((current) => ({ ...current, [presentationId]: text }))
    setRowErrors((current) => {
      if (!(presentationId in current)) return current
      const { [presentationId]: _cleared, ...rest } = current
      return rest
    })
    setNotice(null)
  }

  const toggleRow = (presentationId: string) =>
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(presentationId)) next.delete(presentationId)
      else next.add(presentationId)
      return next
    })

  const toggleAllVisible = () =>
    setSelected((current) => {
      const next = new Set(current)
      for (const row of visibleRows) {
        if (allVisibleSelected) next.delete(row.presentationId)
        else next.add(row.presentationId)
      }
      return next
    })

  const applyRemark = () => {
    const percent = numberFormat.parse(percentText)
    if (percent === null || percent <= -100) {
      setPercentError(t('editor.remark.invalid'))
      return
    }
    setPercentError(null)
    setNotice(null)
    setEdits((current) => {
      const next = { ...current }
      for (const row of remarkTargets) next[row.presentationId] = toInputText(applyPercent(row.base!, percent))
      return next
    })
  }

  // An invalid row may be hidden by the filters: clear them and bring the first one into view.
  const showFirstInvalid = () => {
    const first = invalid[0]
    if (!first) return
    flushSync(() => {
      setSearch('')
      setCategoryId('')
      setPriceFilter('all')
    })
    const input = gridRef.current?.querySelector<HTMLInputElement>(
      `input[data-presentation-id="${first.row.presentationId}"]`,
    )
    input?.scrollIntoView?.({ block: 'center' })
    input?.focus()
  }

  const undoAll = () => {
    setEdits({})
    setRowErrors({})
    setPublishError(null)
    setPercentError(null)
  }

  const publish = async () => {
    const entries = changed.map(({ row, edit }) => ({
      presentationId: row.presentationId,
      unitPrice: (edit as { value: number }).value,
    }))
    setPublishing(true)
    setPublishError(null)
    setNotice(null)
    setRowErrors({})
    setDateError(null)
    try {
      const result = await publishEntriesBatch(priceList.id, { effectiveFrom: effectiveFrom === '' ? null : effectiveFrom, entries })
      // Only what was published is done; any other text stays the operator's.
      const published = new Map(entries.map((entry) => [entry.presentationId, entry.unitPrice]))
      setEdits((current) =>
        Object.fromEntries(
          Object.entries(current).filter(([presentationId, text]) => {
            const value = parseAmount(text.trim())
            return value === null || round2(value) !== published.get(presentationId)
          }),
        ),
      )
      setSelected(new Set())
      setPercentText('')
      setNotice(t('editor.published', { count: result.published }))
      setReloadToken((token) => token + 1)
    } catch (err) {
      const violations = floorViolationsOf(err)
      if (violations) {
        setRowErrors(violationMessages(violations, priceList.id, t))
        setPublishError(t('editor.errors.belowFloor'))
      } else if (err instanceof ApiError && err.status === 400) {
        const { byRow, date, general } = validationMessages(err, entries)
        setRowErrors(byRow)
        if (date.length > 0) setDateError(date.join(' '))
        setPublishError([err.message, ...general].join(' '))
      } else {
        setPublishError(err instanceof ApiError ? err.message : t('editor.errors.unexpectedPublish'))
      }
    } finally {
      setPublishing(false)
    }
  }

  const categoriesInUse = categories.filter((category) => rows.some((row) => row.categoryId === category.id))

  return (
    <div className="flex flex-col gap-4">
      {/* The composition the bases get (IVA, IB, flete, remarcación): a new base shows its new final with it. */}
      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-muted/30 px-3 py-2 text-sm">
        <span className="font-medium">{t('editor.composition.label')}</span>
        <span className="min-w-0 flex-1 text-muted-foreground">
          {components === null ? t('editor.composition.loading') : summarizeComponents(components, t)}
        </span>
        {onEditComposition && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={dirtyCount > 0}
            title={dirtyCount > 0 ? t('editor.composition.publishFirst') : undefined}
            onClick={onEditComposition}
          >
            {t('editor.composition.edit')}
          </Button>
        )}
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <div className="flex min-w-48 flex-1 flex-col gap-1.5 sm:max-w-sm">
          <Label htmlFor="price-editor-search">{t('editor.search.label')}</Label>
          <Input
            id="price-editor-search"
            type="search"
            value={search}
            placeholder={t('editor.search.placeholder')}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <div className="flex w-full flex-col gap-1.5 sm:w-44">
          <Label htmlFor="price-editor-category">{t('editor.categoryLabel')}</Label>
          <Select id="price-editor-category" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">{t('editor.allCategories')}</option>
            {categoriesInUse.map((category) => (
              <option key={category.id} value={category.id}>
                {category.name}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex w-full flex-col gap-1.5 sm:w-44">
          <Label htmlFor="price-editor-price-filter">{t('editor.priceFilter.label')}</Label>
          <Select id="price-editor-price-filter" value={priceFilter} onChange={(e) => setPriceFilter(e.target.value as PriceFilter)}>
            <option value="all">{t('editor.priceFilter.all')}</option>
            <option value="missing">{t('editor.priceFilter.missing', { count: missingCount })}</option>
            <option value="priced">{t('editor.priceFilter.priced')}</option>
          </Select>
        </div>
        <div className="flex w-full flex-col gap-1.5 sm:w-44">
          <Label htmlFor="price-editor-effective-from">{t('editor.effectiveFromLabel')}</Label>
          <Input
            id="price-editor-effective-from"
            type="date"
            value={effectiveFrom}
            min={businessDay ?? todayIso()}
            disabled={publishing}
            placeholder={t('editor.effectiveFromToday')}
            aria-invalid={dateError ? true : undefined}
            aria-describedby={
              dateError
                ? 'price-editor-effective-from-hint price-editor-effective-from-error'
                : 'price-editor-effective-from-hint'
            }
            onChange={(e) => {
              setEffectiveFrom(e.target.value)
              setDateError(null)
            }}
          />
        </div>
        {/* The date's hint and error sit under the row, so every field of the row lines up. */}
        <div className="w-full">
          <p id="price-editor-effective-from-hint" className="text-xs text-muted-foreground">
            {t('editor.effectiveFromHint')}
          </p>
          {dateError && (
            <p id="price-editor-effective-from-error" className="text-xs text-destructive">
              {dateError}
            </p>
          )}
        </div>
      </div>

      <div className="flex flex-wrap items-end gap-3 rounded-lg border border-border bg-card p-3">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="price-editor-percent">{t('editor.remark.label')}</Label>
          <Input
            id="price-editor-percent"
            inputMode="decimal"
            className="w-28"
            value={percentText}
            disabled={publishing}
            placeholder={t('editor.remark.placeholder')}
            aria-invalid={percentError ? true : undefined}
            onChange={(e) => {
              setPercentText(e.target.value)
              setPercentError(null)
            }}
          />
        </div>
        <Button
          type="button"
          variant="outline"
          onClick={applyRemark}
          disabled={publishing || baselineStale || percentText.trim() === '' || remarkTargets.length === 0}
        >
          {t('editor.remark.apply')}
        </Button>
        <div className="min-w-0 flex-1 basis-60 text-xs">
          <p className="font-medium" role="status">
            {t(`editor.remark.scope.${remarkScope}`, { count: remarkTargets.length })}
          </p>
          <p className="text-muted-foreground">{t('editor.remark.hint')}</p>
        </div>
        {percentError && <p className="w-full text-xs text-destructive">{percentError}</p>}
      </div>

      {loadError && (
        <div
          role="alert"
          className="flex flex-wrap items-center gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
        >
          <p className="min-w-0 flex-1">{loadError}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => setReloadToken((token) => token + 1)}>
            {tCommon('actions.retry')}
          </Button>
        </div>
      )}
      {loading && rows.length > 0 && (
        <p role="status" className="text-sm text-muted-foreground">
          {t('editor.loadingBaseline')}
        </p>
      )}

      <div ref={gridRef} data-testid="price-editor-grid" className="w-full overflow-x-auto rounded-lg border border-border bg-card">
        <table className="w-full min-w-[760px] text-left text-sm">
          <thead className="border-b border-border text-xs text-muted-foreground uppercase">
            <tr>
              <th scope="col" className="w-10 px-3 py-2">
                <input
                  type="checkbox"
                  aria-label={t('editor.selectAll')}
                  checked={allVisibleSelected}
                  disabled={publishing || visibleRows.length === 0}
                  onChange={toggleAllVisible}
                />
              </th>
              <th scope="col" className="px-3 py-2 font-medium">{t('editor.columns.product')}</th>
              <th scope="col" className="px-3 py-2 font-medium">{t('editor.columns.presentation')}</th>
              <th scope="col" className="px-3 py-2 font-medium">{t('editor.columns.code')}</th>
              <th scope="col" className="px-3 py-2 font-medium">{t('editor.columns.category')}</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">{t('editor.columns.base')}</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">{t('editor.columns.final')}</th>
              <th scope="col" className="px-3 py-2 font-medium">{t('editor.columns.newBase')}</th>
              <th scope="col" className="px-3 py-2">
                <span className="sr-only">{t('editor.columns.history')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {loading && rows.length === 0 ? (
              <tr>
                <td colSpan={9} className="px-3 py-6 text-center text-muted-foreground">
                  {t('editor.loading')}
                </td>
              </tr>
            ) : visibleRows.length === 0 ? (
              <tr>
                <td colSpan={9} className="px-3 py-6 text-center text-muted-foreground">
                  {rows.length === 0 ? t('editor.empty') : t('editor.noMatch')}
                </td>
              </tr>
            ) : (
              visibleRows.map((row) => (
                <EditorGridRow
                  key={row.presentationId}
                  row={row}
                  text={edits[row.presentationId] ?? ''}
                  edit={editOf(row)}
                  serverError={rowErrors[row.presentationId] ?? null}
                  selected={selected.has(row.presentationId)}
                  baselineStale={baselineStale}
                  newFinal={(base) => (components === null ? null : composeFinal(base, components))}
                  locked={publishing}
                  formatPercent={(value) => `${numberFormat.formatSigned(Math.round(value * 10) / 10)} %`}
                  onToggle={() => toggleRow(row.presentationId)}
                  onEdit={(text) => setEdit(row.presentationId, text)}
                  onHistory={() => setHistoryRow(row)}
                />
              ))
            )}
          </tbody>
        </table>
      </div>

      <div className="sticky bottom-0 z-10 flex flex-col gap-2 rounded-lg border border-border bg-card p-3 shadow-md">
        {notice && (
          <p role="status" className="text-sm">
            {notice}
          </p>
        )}
        {publishError && (
          <p role="alert" className="text-sm text-destructive">
            {publishError}
          </p>
        )}
        <div className="flex flex-wrap items-center gap-2">
          <p className="text-sm font-medium">{t('editor.changedCount', { count: changed.length })}</p>
          {invalid.length > 0 && (
            <>
              <p className="text-sm text-destructive">{t('editor.invalidCount', { count: invalid.length })}</p>
              <Button type="button" variant="outline" size="sm" onClick={showFirstInvalid}>
                {t('editor.showInvalid')}
              </Button>
            </>
          )}
          <span className="mr-auto" />
          <Button type="button" variant="outline" onClick={undoAll} disabled={publishing || Object.keys(edits).length === 0}>
            {t('editor.undo')}
          </Button>
          <Button type="button" onClick={() => void publish()} disabled={!canPublish}>
            {publishing ? t('editor.publishing') : t('editor.publish')}
          </Button>
        </div>
      </div>

      {historyRow && (
        <HistoryPanel priceListId={priceList.id} row={historyRow} onClose={() => setHistoryRow(null)} />
      )}
    </div>
  )
}

function EditorGridRow({
  row,
  text,
  edit,
  serverError,
  selected,
  baselineStale,
  newFinal,
  locked,
  formatPercent,
  onToggle,
  onEdit,
  onHistory,
}: {
  row: EditorRow
  text: string
  edit: RowEdit
  serverError: string | null
  selected: boolean
  /** The row's current prices belong to another date: they are not shown, nor compared against. */
  baselineStale: boolean
  /** The final price a base gets under the list's composition; null while the composition is unknown. */
  newFinal: (base: number) => number | null
  /** While a publish is in flight nothing the batch reads can change. */
  locked: boolean
  formatPercent: (value: number) => string
  onToggle: () => void
  onEdit: (text: string) => void
  onHistory: () => void
}) {
  const { t } = useTranslation('priceLists')
  const inputId = useId()
  const messageId = useId()
  const invalid = edit.status === 'invalid' || serverError !== null
  return (
    <tr
      data-changed={edit.status === 'changed' ? 'true' : undefined}
      className={`border-b border-border align-top last:border-0 ${edit.status === 'changed' ? 'bg-primary/5' : ''}`}
    >
      <td className="px-3 py-2">
        <input
          type="checkbox"
          aria-label={t('editor.selectRow', { name: row.label })}
          checked={selected}
          disabled={locked}
          onChange={onToggle}
        />
      </td>
      <td className="px-3 py-2 font-medium break-words">{row.productName}</td>
      <td className="px-3 py-2 text-muted-foreground">{row.showPresentation ? row.presentationName : ''}</td>
      <td className="px-3 py-2 tabular-nums">{row.code ?? '—'}</td>
      <td className="px-3 py-2">{row.categoryName ?? '—'}</td>
      <td className="px-3 py-2 text-right tabular-nums">
        {baselineStale ? '…' : row.base === null ? '—' : formatMoney(row.base)}
      </td>
      <td className="px-3 py-2 text-right tabular-nums">
        {baselineStale ? '…' : row.final === null ? '—' : formatMoney(row.final)}
      </td>
      <td className="px-3 py-2">
        <div className="flex min-w-40 flex-col gap-1">
          <Input
            id={inputId}
            inputMode="decimal"
            className="h-8 w-32"
            aria-label={t('editor.newBaseOf', { name: row.label })}
            aria-invalid={invalid ? true : undefined}
            aria-describedby={
              (edit.status === 'changed' && !baselineStale) || edit.status === 'invalid' || serverError ? messageId : undefined
            }
            data-presentation-id={row.presentationId}
            value={text}
            disabled={locked}
            onChange={(e) => onEdit(e.target.value)}
          />
          <div id={messageId} className="flex flex-col gap-0.5 text-xs">
            {edit.status === 'changed' && !baselineStale && (
              <span className="flex flex-wrap gap-x-2">
                <span className="tabular-nums">
                  {row.base === null
                    ? t('editor.firstPrice', { price: formatMoney(edit.value) })
                    : `${formatMoney(row.base)} → ${formatMoney(edit.value)}`}
                </span>
                {row.base !== null && (
                  <span className="font-medium tabular-nums">{formatPercent(differencePercent(row.base, edit.value))}</span>
                )}
              </span>
            )}
            {edit.status === 'changed' && !baselineStale && newFinal(edit.value) !== null && (
              <span className="tabular-nums text-foreground">
                {row.final === null
                  ? t('editor.newFinal', { price: formatMoney(newFinal(edit.value)!) })
                  : t('editor.finalChange', { from: formatMoney(row.final), to: formatMoney(newFinal(edit.value)!) })}
              </span>
            )}
            {edit.status === 'invalid' && <span className="text-destructive">{t('editor.errors.invalidPrice')}</span>}
            {serverError && <span className="text-destructive">{serverError}</span>}
          </div>
        </div>
      </td>
      <td className="px-3 py-2 text-right">
        <Button type="button" variant="outline" size="sm" aria-label={t('editor.historyOf', { name: row.label })} onClick={onHistory}>
          {t('history.show')}
        </Button>
      </td>
    </tr>
  )
}

/** The history of one row, beside the grid: opening it never moves a row or its buttons. */
function HistoryPanel({ priceListId, row, onClose }: { priceListId: string; row: EditorRow; onClose: () => void }) {
  const { t } = useTranslation('priceLists')
  const { t: tCommon } = useTranslation('common')
  const titleId = useId()

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', closeOnEscape)
    return () => document.removeEventListener('keydown', closeOnEscape)
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-black/30" onClick={onClose}>
      <aside
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="flex h-full w-full max-w-sm flex-col gap-4 overflow-y-auto border-l border-border bg-card p-5 shadow-lg"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <h2 id={titleId} className="text-base font-semibold break-words">
              {t('editor.historyOf', { name: row.label })}
            </h2>
            {row.code && <p className="text-xs text-muted-foreground">{row.code}</p>}
          </div>
          <Button type="button" variant="outline" size="sm" onClick={onClose}>
            {tCommon('actions.close')}
          </Button>
        </div>
        <PriceHistory key={row.presentationId} priceListId={priceListId} presentationId={row.presentationId} />
      </aside>
    </div>
  )
}

/**
 * The grid rows: every active presentation whose product is known, plus any presentation the list
 * still prices but the catalog no longer lists (named from the breakdown). Ordered by product, then
 * presentation.
 */
function buildRows(
  presentations: PresentationRecord[],
  products: ProductRecord[],
  categories: CategoryRecord[],
  items: BreakdownItem[],
): EditorRow[] {
  const productsById = new Map(products.map((product) => [product.id, product]))
  const categoriesById = new Map(categories.map((category) => [category.id, category]))
  const itemsById = new Map(items.map((item) => [item.presentationId, item]))

  const drafts: Omit<EditorRow, 'showPresentation' | 'label'>[] = []
  const productKeys = new Map<string, string>()
  for (const presentation of presentations) {
    const product = productsById.get(presentation.productId)
    const item = itemsById.get(presentation.id)
    if (!product && !item) continue
    productKeys.set(presentation.id, presentation.productId)
    drafts.push({
      presentationId: presentation.id,
      productName: product?.name ?? item!.productName,
      presentationName: presentation.name,
      code: presentation.identificationCode,
      categoryId: product?.categoryId ?? null,
      categoryName: product ? (categoriesById.get(product.categoryId)?.name ?? null) : null,
      base: item?.base ?? null,
      final: item?.final ?? null,
    })
  }
  for (const item of items) {
    if (productKeys.has(item.presentationId)) continue
    const product = productsById.get(item.productId)
    productKeys.set(item.presentationId, item.productId)
    drafts.push({
      presentationId: item.presentationId,
      productName: product?.name ?? item.productName,
      presentationName: item.presentationName,
      code: item.identificationCode,
      categoryId: product?.categoryId ?? null,
      categoryName: product ? (categoriesById.get(product.categoryId)?.name ?? null) : null,
      base: item.base,
      final: item.final,
    })
  }

  const perProduct = new Map<string, number>()
  for (const productId of productKeys.values()) perProduct.set(productId, (perProduct.get(productId) ?? 0) + 1)

  return drafts
    .map((draft) => {
      const showPresentation = (perProduct.get(productKeys.get(draft.presentationId)!) ?? 0) > 1
      return {
        ...draft,
        showPresentation,
        label: showPresentation ? `${draft.productName} · ${draft.presentationName}` : draft.productName,
      }
    })
    .sort(
      (a, b) =>
        a.productName.localeCompare(b.productName, 'es') || a.presentationName.localeCompare(b.presentationName, 'es'),
    )
}

/** One message per offending row; a violation in a list that depends on this one names that list. */
function violationMessages(
  violations: FloorViolation[],
  priceListId: string,
  t: ReturnType<typeof useTranslation<'priceLists'>>['t'],
): Record<string, string> {
  const messages: Record<string, string[]> = {}
  for (const violation of violations) {
    const params = {
      price: formatMoney(violation.price),
      floor: violation.floorPriceListName,
      floorPrice: formatMoney(violation.floorPrice),
      list: violation.priceListName,
    }
    const message =
      violation.priceListId === priceListId ? t('editor.violation', params) : t('editor.dependentViolation', params)
    ;(messages[violation.presentationId] ??= []).push(message)
  }
  return Object.fromEntries(Object.entries(messages).map(([id, list]) => [id, list.join(' ')]))
}

/**
 * A 400 names the offending entry by its index in the batch (`entries[3].unitPrice`); those land on
 * their row, a refused `effectiveFrom` lands on the date field, anything else (the batch size) stays general.
 */
function validationMessages(
  error: ApiError,
  entries: { presentationId: string }[],
): { byRow: Record<string, string>; date: string[]; general: string[] } {
  const byRow: Record<string, string> = {}
  const date: string[] = []
  const general: string[] = []
  for (const [field, messages] of Object.entries(error.fieldErrors ?? {})) {
    if (field.toLowerCase() === 'effectivefrom') {
      date.push(...messages)
      continue
    }
    const index = /^entries\[(\d+)\]/i.exec(field)?.[1]
    const entry = index === undefined ? undefined : entries[Number(index)]
    if (entry) byRow[entry.presentationId] = [byRow[entry.presentationId], ...messages].filter(Boolean).join(' ')
    else general.push(...messages)
  }
  return { byRow, date, general }
}
