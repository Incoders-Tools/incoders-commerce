import { Fragment, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import type { DataViewMode } from './ViewSwitch'

export interface DataViewColumn<T> {
  /** Stable key, also used as the field label in the card layout. */
  key: string
  header: string
  cell: (item: T) => ReactNode
  /** Hide this column below `md:` in the table layout. */
  hideOnMobile?: boolean
  /** Skip this field in the card layout (it is already the card title). */
  hideInCards?: boolean
}

export interface DataViewProps<T> {
  items: T[]
  columns: DataViewColumn<T>[]
  getRowKey: (item: T) => string
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
  /** Extra content under an item (e.g. an inline edit form). Return null to skip. */
  renderExpanded?: (item: T) => ReactNode
  className?: string
}

/**
 * T4: the shared list surface for data screens. Deliberately NOT a table
 * framework — no sorting/pagination/column-resizing abstraction — just the
 * three states every screen needs (loading, empty, populated) rendered
 * either as a table or as a responsive card grid.
 */
export function DataView<T>({
  items,
  columns,
  getRowKey,
  view,
  loading = false,
  emptyMessage,
  loadErrorMessage = null,
  loadingMessage = 'Loading…',
  renderActions,
  renderExpanded,
  className,
}: DataViewProps<T>) {
  if (loading) {
    return (
      <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
        {loadingMessage}
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

  if (view === 'cards') {
    return (
      <div className={cn('grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4', className)}>
        {items.map((item) => {
          const expanded = renderExpanded?.(item)
          const [title, ...rest] = columns
          return (
            <article
              key={getRowKey(item)}
              data-testid="data-view-card"
              className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 text-card-foreground shadow-sm"
            >
              <p className="font-medium break-words">{title.cell(item)}</p>
              <dl className="flex flex-col gap-1 text-sm">
                {rest
                  .filter((column) => !column.hideInCards)
                  .map((column) => (
                    <div key={column.key} className="flex items-baseline justify-between gap-3">
                      <dt className="text-xs text-muted-foreground">{column.header}</dt>
                      <dd className="text-right break-words">{column.cell(item)}</dd>
                    </div>
                  ))}
              </dl>
              {renderActions && <div className="flex flex-wrap items-center gap-2">{renderActions(item)}</div>}
              {expanded}
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
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                className={cn('px-4 py-3 font-medium', column.hideOnMobile && 'hidden md:table-cell')}
              >
                {column.header}
              </th>
            ))}
            {renderActions && (
              <th scope="col" className="px-4 py-3 text-right font-medium">
                <span className="sr-only">Actions</span>
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {items.map((item) => {
            const expanded = renderExpanded?.(item)
            const columnCount = columns.length + (renderActions ? 1 : 0)
            return (
              <Fragment key={getRowKey(item)}>
                <tr className="border-b border-border last:border-0 hover:bg-muted/50">
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
                {expanded && (
                  <tr className="border-b border-border last:border-0 bg-muted/30">
                    <td colSpan={columnCount} className="px-4 py-3">
                      {expanded}
                    </td>
                  </tr>
                )}
              </Fragment>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
