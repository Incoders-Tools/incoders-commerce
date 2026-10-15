import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { MoreHorizontal } from 'lucide-react'
import { Button, buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

/** One action of a list row: a button (`onSelect`) or a link (`to`). Hidden ones are left out. */
export interface RowAction {
  key: string
  label: string
  onSelect?: () => void
  to?: string
  /** Router state handed to the `to` page. */
  state?: unknown
  hidden?: boolean
  disabled?: boolean
  destructive?: boolean
}

/** Up to this many actions show as buttons; with more, the first stays a button and the rest go to the "…" menu. */
export const MAX_INLINE_ACTIONS = 2

/**
 * The actions of one list row. Two or fewer are plain buttons; three or more keep the first (the row's main action) as a
 * button and fold the rest into a "…" menu, so a row never grows into a wall of buttons.
 *
 * The menu renders in a portal with fixed coordinates: the table scrolls horizontally (`overflow-x-auto`), which would
 * clip an absolutely positioned popup. Escape, a click outside, a scroll or a resize close it; the arrow keys move
 * between its items.
 */
export function RowActions({ actions, label }: { actions: readonly RowAction[]; label: string }) {
  const visible = actions.filter((action) => !action.hidden)
  if (visible.length <= MAX_INLINE_ACTIONS) {
    return (
      <>
        {visible.map((action) => (
          <InlineAction key={action.key} action={action} />
        ))}
      </>
    )
  }

  const [main, ...rest] = visible
  return (
    <>
      <InlineAction action={main} />
      <ActionsMenu actions={rest} label={label} />
    </>
  )
}

function InlineAction({ action }: { action: RowAction }) {
  const variant = action.destructive ? 'destructive' : 'outline'
  if (action.to && !action.disabled) {
    return (
      <Link to={action.to} state={action.state} className={buttonVariants({ variant, size: 'sm' })}>
        {action.label}
      </Link>
    )
  }

  return (
    <Button type="button" variant={variant} size="sm" disabled={action.disabled} onClick={action.onSelect}>
      {action.label}
    </Button>
  )
}

const MENU_WIDTH = 224

function ActionsMenu({ actions, label }: { actions: readonly RowAction[]; label: string }) {
  const { t } = useTranslation('common')
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  useLayoutEffect(() => {
    if (!open || !triggerRef.current) return
    const rect = triggerRef.current.getBoundingClientRect()
    const left = Math.max(8, Math.min(rect.right - MENU_WIDTH, window.innerWidth - MENU_WIDTH - 8))
    const height = menuRef.current?.offsetHeight ?? 0
    // Below the trigger, or above it when it would leave the viewport.
    const top = rect.bottom + 4 + height > window.innerHeight && rect.top - 4 - height > 0 ? rect.top - 4 - height : rect.bottom + 4
    setPosition({ top, left })
  }, [open])

  useEffect(() => {
    if (!open) return
    menuRef.current?.querySelector<HTMLElement>('[role="menuitem"]:not([disabled])')?.focus()

    const close = () => setOpen(false)
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false)
        triggerRef.current?.focus()
      }
    }
    const onMouseDown = (event: MouseEvent) => {
      const target = event.target as Node
      if (!menuRef.current?.contains(target) && !triggerRef.current?.contains(target)) setOpen(false)
    }
    document.addEventListener('keydown', onKeyDown)
    document.addEventListener('mousedown', onMouseDown)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      document.removeEventListener('mousedown', onMouseDown)
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [open])

  const moveFocus = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
    event.preventDefault()
    const items = Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([disabled])') ?? [])
    const index = items.indexOf(document.activeElement as HTMLElement)
    const next = event.key === 'ArrowDown' ? (index + 1) % items.length : (index - 1 + items.length) % items.length
    items[next]?.focus()
  }

  const select = (action: RowAction) => {
    setOpen(false)
    action.onSelect?.()
  }

  return (
    <>
      <Button
        ref={triggerRef}
        type="button"
        variant="outline"
        size="sm"
        aria-label={t('dataView.actionsMenu', { name: label })}
        title={t('dataView.moreActions')}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
      >
        <MoreHorizontal aria-hidden="true" className="size-4" />
      </Button>
      {open &&
        createPortal(
          <div
            ref={menuRef}
            role="menu"
            aria-label={t('dataView.actionsMenu', { name: label })}
            onKeyDown={moveFocus}
            style={{ position: 'fixed', top: position?.top ?? -9999, left: position?.left ?? -9999, width: MENU_WIDTH }}
            className="z-50 overflow-hidden rounded-md border border-border bg-card p-1 text-card-foreground shadow-md"
          >
            {actions.map((action) => {
              const className = cn(
                'flex w-full items-center rounded-sm px-2 py-1.5 text-left text-sm transition-colors hover:bg-accent hover:text-accent-foreground focus:bg-accent focus:outline-none disabled:pointer-events-none disabled:opacity-50',
                action.destructive ? 'text-destructive' : 'text-foreground',
              )
              // A link only where there is one: a screen without links needs no router.
              return action.to && !action.disabled ? (
                <Link key={action.key} to={action.to} state={action.state} role="menuitem" onClick={() => setOpen(false)} className={className}>
                  {action.label}
                </Link>
              ) : (
                <button key={action.key} type="button" role="menuitem" disabled={action.disabled} onClick={() => select(action)} className={className}>
                  {action.label}
                </button>
              )
            })}
          </div>,
          document.body,
        )}
    </>
  )
}
