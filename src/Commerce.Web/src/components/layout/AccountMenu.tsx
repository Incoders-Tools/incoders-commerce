import { useEffect, useRef, useState, type SVGProps } from 'react'
import { Link } from 'react-router'
import { cn } from '@/lib/utils'
import { useAuth } from '@/auth/AuthContext'
import { ThemeSwitcher } from '@/components/theme/ThemeSwitcher'

function ChevronDownIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>
      <path d="m6 9 6 6 6-6" />
    </svg>
  )
}

/**
 * Account-scoped dropdown (T3): trigger shows the signed-in user's display
 * name (no `email` field exists on `SignedInResponse` — see
 * `api/types.ts`); contents are "Change password" (moved off the flat nav
 * bar), the theme switcher (moved out of the header, see `AppLayout`), and
 * "Sign out". No headless-UI/Radix dependency is installed, so this is a
 * small hand-built primitive: Escape and outside-click both close it,
 * mirroring the accessibility behavior those libraries provide for free.
 */
export function AccountMenu() {
  const { user, signOut } = useAuth()
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setOpen(false)
    }

    function handleClickOutside(event: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
        setOpen(false)
      }
    }

    document.addEventListener('keydown', handleKeyDown)
    document.addEventListener('mousedown', handleClickOutside)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [open])

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((prev) => !prev)}
        className="flex items-center gap-1.5 rounded-md px-2 py-1.5 text-sm font-medium text-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
      >
        <span>{user!.displayName}</span>
        <ChevronDownIcon className={cn('transition-transform', open && 'rotate-180')} />
      </button>

      {open && (
        <div
          role="menu"
          aria-label="Account"
          className="absolute right-0 top-full z-50 mt-2 w-60 overflow-hidden rounded-md border border-border bg-card text-card-foreground shadow-md"
        >
          <div className="border-b border-border px-3 py-2">
            <p className="truncate text-sm font-medium text-foreground">{user!.displayName}</p>
          </div>
          <div className="p-1">
            <Link
              role="menuitem"
              to="/app/password"
              onClick={() => setOpen(false)}
              className="flex items-center rounded-sm px-2 py-1.5 text-sm text-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            >
              Change password
            </Link>
          </div>
          <div className="border-t border-border p-2">
            <ThemeSwitcher />
          </div>
          <div className="border-t border-border p-1">
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false)
                void signOut()
              }}
              className="flex w-full items-center rounded-sm px-2 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            >
              Sign out
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
