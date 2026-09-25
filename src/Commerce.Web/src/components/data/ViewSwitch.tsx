import type { SVGProps } from 'react'
import { cn } from '@/lib/utils'

/** The two layouts every data screen offers. Stored verbatim in localStorage. */
export type DataViewMode = 'table' | 'cards'

// No icon library is installed (see ThemeSwitcher.tsx) — inline SVGs keep
// the dependency list unchanged for two glyphs.
function TableIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>
      <path d="M3 5h18M3 12h18M3 19h18" />
    </svg>
  )
}

function CardsIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>
      <rect x="3" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="14" width="7" height="7" rx="1.5" />
    </svg>
  )
}

const OPTIONS: { value: DataViewMode; label: string; Icon: (props: SVGProps<SVGSVGElement>) => React.JSX.Element }[] = [
  { value: 'table', label: 'Table view', Icon: TableIcon },
  { value: 'cards', label: 'Card view', Icon: CardsIcon },
]

/**
 * T4: segmented table/cards switch. Deliberately mirrors
 * `components/theme/ThemeSwitcher.tsx` (same `role="radiogroup"` +
 * `aria-checked` buttons, same token classes) so both controls read as one
 * design system. Native buttons keep it tab- and Enter/Space-operable
 * without extra key handling.
 */
export function ViewSwitch({
  value,
  onChange,
  className,
}: {
  value: DataViewMode
  onChange: (view: DataViewMode) => void
  className?: string
}) {
  return (
    <div
      role="radiogroup"
      aria-label="View"
      className={cn('inline-flex items-center gap-0.5 rounded-md border border-border bg-muted p-0.5', className)}
    >
      {OPTIONS.map(({ value: option, label, Icon }) => {
        const selected = value === option
        return (
          <button
            key={option}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={label}
            title={label}
            onClick={() => onChange(option)}
            className={cn(
              'inline-flex h-7 items-center gap-1.5 rounded-sm px-2.5 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
              selected ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground',
            )}
          >
            <Icon />
          </button>
        )
      })}
    </div>
  )
}
