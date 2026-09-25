import { useId, type ReactNode } from 'react'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'
import { ViewSwitch, type DataViewMode } from './ViewSwitch'

/**
 * T4: filter bar for a data screen — a client-side search field plus the
 * table/cards switch. On mobile the field takes the full width and the
 * switch drops below it; from `sm:` up they share one row.
 */
export function DataToolbar({
  searchValue,
  onSearchChange,
  searchLabel,
  searchPlaceholder = 'Search…',
  view,
  onViewChange,
  children,
  className,
}: {
  searchValue: string
  onSearchChange: (value: string) => void
  searchLabel: string
  searchPlaceholder?: string
  view: DataViewMode
  onViewChange: (view: DataViewMode) => void
  /** Extra filters rendered between the search field and the view switch. */
  children?: ReactNode
  className?: string
}) {
  const searchId = useId()

  return (
    <div className={cn('flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between', className)}>
      <div className="flex w-full flex-col gap-3 sm:max-w-sm">
        <label htmlFor={searchId} className="sr-only">
          {searchLabel}
        </label>
        <Input
          id={searchId}
          type="search"
          value={searchValue}
          placeholder={searchPlaceholder}
          onChange={(event) => onSearchChange(event.target.value)}
        />
      </div>
      <div className="flex items-center gap-2">
        {children}
        <ViewSwitch value={view} onChange={onViewChange} />
      </div>
    </div>
  )
}
