import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'
import type { DataViewMode } from './ViewSwitch'

export interface DataViewColumn<T> {
  /** Stable key, also used as the field label in the card layout. */
  key: string
  header: string
  /** Optional tooltip explaining the column, shown on its header (`title`). */
  headerHint?: string
  cell: (item: T) => ReactNode
  /** Hide this column below `md:` in the table layout. */
  hideOnMobile?: boolean
  /** Skip this field in the card layout (it is already the card title). */
  hideInCards?: boolean
  /** Makes the column sortable (with `sort`/`onSortChange` on the view); null/undefined always sort last. */
  sortValue?: (item: T) => string | number | null | undefined
}

export interface DataViewSort {
  key: string
  direction: 'asc' | 'desc'
}

/** A click on a header: a new column starts ascending, the same column flips. */
export function nextSort(current: DataViewSort | null, key: string): DataViewSort {
  if (current?.key === key) return { key, direction: current.direction === 'asc' ? 'desc' : 'asc' }
  return { key, direction: 'asc' }
}

const textOrder = new Intl.Collator('es', { sensitivity: 'base', numeric: true })

function sortItems<T>(items: T[], columns: DataViewColumn<T>[], sort: DataViewSort | null | undefined): T[] {
  const sortValue = sort ? columns.find((column) => column.key === sort.key)?.sortValue : undefined
  if (!sort || !sortValue) return items
  const sign = sort.direction === 'asc' ? 1 : -1
  return [...items].sort((a, b) => {
    const left = sortValue(a)
    const right = sortValue(b)
    const leftEmpty = left === null || left === undefined
    const rightEmpty = right === null || right === undefined
    if (leftEmpty || rightEmpty) return leftEmpty === rightEmpty ? 0 : leftEmpty ? 1 : -1
    if (typeof left === 'number' && typeof right === 'number') return sign * (left - right)
    return sign * textOrder.compare(String(left), String(right))
  })
}

export interface DataViewProps<T> {
  items: T[]
  columns: DataViewColumn<T>[]
  getRowKey: (item: T) => string
  /** Optional DOM id of a row/card, so other content can link to it (`#id`). */
  getRowId?: (item: T) => string | undefined
  view: DataViewMode
  loading?: boolean
  emptyMessage: string
  /**
   * Why the collection could not be read, when the last load failed. Replaces
   * `emptyMessage` while there is nothing to show: "there are none" and "I
   * could not fetch them" are different answers, and rendering the first one
   * after a failed load tells the operator something untrue. Only a LOAD
   * failure belongs here — an error from some unrelated action says nothing
   * about whether the collection is empty.
   */
  loadErrorMessage?: string | null
  loadingMessage?: string
  /** Per-item action buttons (right-aligned in the table, footer in cards). */
  renderActions?: (item: T) => ReactNode
  className?: string
  /**
   * Opt-in sorting, owned by the screen so it survives the view being swapped out (e.g. for a form): the items are
   * shown in this order, and the headers of the columns with a `sortValue` become buttons that report `nextSort`.
   */
  sort?: DataViewSort | null
  onSortChange?: (sort: DataViewSort) => void
}

/**
 * T4: the shared list surface for data screens. Deliberately NOT a table
 * framework — no pagination/column-resizing abstraction — just the three
 * states every screen needs (loading, empty, populated) rendered either as a
 * table or as a responsive card grid. Sorting is opt-in and client-side
 * (owner-requests-quick-adjustments Q6): only for lists the server returns
 * whole.
 *
 * T9: dropped `renderExpanded` (a boxed inline row/card editor) — its one
 * caller, `CatalogScreen`'s "Edit code", now swaps to a full-screen
 * `FormPage` instead, and no other screen ever adopted the prop. A future
 * inline expansion can be reintroduced deliberately; it should not come
 * back as a side effect of copying this one.
 */
export function DataView<T>({
  items,
  columns,
  getRowKey,
  getRowId,
  view,
  loading = false,
  emptyMessage,
  loadErrorMessage = null,
  loadingMessage,
  renderActions,
  className,
  sort = null,
  onSortChange,
}: DataViewProps<T>) {
  const { t } = useTranslation('common')
  const resolvedLoadingMessage = loadingMessage ?? t('dataView.loading')
  if (loading) {
    return (
      <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
        {resolvedLoadingMessage}
      </p>
    )
  }

  if (items.length === 0) {
    // Stale items still on screen (a failed RELOAD) keep rendering below: the
    // screen's own alert reports the failure, and showing the last known rows
    // beats blanking them.
    if (loadErrorMessage) {
      return (
        <p
          data-testid="data-view-load-error"
          className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-10 text-center text-sm text-destructive"
        >
          {loadErrorMessage}
        </p>
      )
    }

    return (
      <p className="rounded-lg border border-dashed border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
        {emptyMessage}
      </p>
    )
  }

  const shown = sortItems(items, columns, sort)

  if (view === 'cards') {
    return (
      <div className={cn('grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4', className)}>
        {shown.map((item) => {
          const [title, ...rest] = columns
          return (
            <article
              key={getRowKey(item)}
              id={getRowId?.(item)}
              data-testid="data-view-card"
              className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 text-card-foreground shadow-sm"
            >
              <p className="font-medium break-words">{title.cell(item)}</p>
              <dl className="flex flex-col gap-1 text-sm">
                {rest
                  .filter((column) => !column.hideInCards)
                  .map((column) => (
                    <div key={column.key} className="flex items-baseline justify-between gap-3">
                      <dt className="text-xs text-muted-foreground" title={column.headerHint}>{column.header}</dt>
                      <dd className="text-right break-words">{column.cell(item)}</dd>
                    </div>
                  ))}
              </dl>
              {renderActions && <div className="flex flex-wrap items-center gap-2">{renderActions(item)}</div>}
            </article>
          )
        })}
      </div>
    )
  }

  return (
    <div className={cn('w-full overflow-x-auto rounded-lg border border-border bg-card', className)}>
      <table className="w-full text-left text-sm">
        <thead className="border-b border-border text-xs text-muted-foreground uppercase">
          <tr>
            {columns.map((column) => {
              const sortable = column.sortValue !== undefined && onSortChange !== undefined
              const direction = sort?.key === column.key ? sort.direction : null
              return (
                <th
                  key={column.key}
                  scope="col"
                  title={column.headerHint}
                  aria-sort={sortable ? (direction === 'asc' ? 'ascending' : direction === 'desc' ? 'descending' : 'none') : undefined}
                  className={cn('px-4 py-3 font-medium', column.hideOnMobile && 'hidden md:table-cell')}
                >
                  {sortable ? (
                    <button
                      type="button"
                      title={t('dataView.sortBy', { column: column.header })}
                      className="inline-flex items-center gap-1 uppercase hover:text-foreground"
                      onClick={() => onSortChange(nextSort(sort, column.key))}
                    >
                      {column.header}
                      <span aria-hidden="true">{direction === 'asc' ? '▲' : direction === 'desc' ? '▼' : '↕'}</span>
                    </button>
                  ) : (
                    column.header
                  )}
                </th>
              )
            })}
            {renderActions && (
              <th scope="col" className="px-4 py-3 text-right font-medium">
                <span className="sr-only">{t('dataView.actions')}</span>
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {shown.map((item) => {
            return (
              <tr key={getRowKey(item)} id={getRowId?.(item)} className="border-b border-border last:border-0 hover:bg-muted/50">
                {columns.map((column) => (
                  <td
                    key={column.key}
                    className={cn('px-4 py-3 align-middle', column.hideOnMobile && 'hidden md:table-cell')}
                  >
                    {column.cell(item)}
                  </td>
                ))}
                {renderActions && (
                  <td className="px-4 py-3 text-right">
                    <div className="flex flex-wrap items-center justify-end gap-2">{renderActions(item)}</div>
                  </td>
                )}
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
