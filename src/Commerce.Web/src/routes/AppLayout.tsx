import { useState, type ReactNode, type SVGProps } from 'react'
import { NavLink, Outlet } from 'react-router'
import { cn } from '@/lib/utils'
import { hasPermission, useAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import { AccountMenu } from '@/components/layout/AccountMenu'

function MenuIcon(props: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" {...props}>
      <path d="M4 6h16M4 12h16M4 18h16" />
    </svg>
  )
}

/**
 * Enterprise app shell (T3): fixed sidebar nav on desktop, hamburger-toggled
 * off-canvas sidebar on mobile, and a full-width content area (no more
 * `max-w-3xl` centered column — user feedback was the app "doesn't use the
 * full screen"). Replaces the former header/`NavTab` bar/`Outlet` layout
 * from T1/T2. "Change password" moved out of the flat nav bar into
 * `AccountMenu`; the theme switcher moved there too (was mounted directly
 * in the header as a T2 placeholder).
 */
export function AppLayout() {
  const { user } = useAuth()
  const [mobileOpen, setMobileOpen] = useState(false)

  const closeMobileNav = () => setMobileOpen(false)

  return (
    <div className="flex min-h-screen bg-background text-foreground">
      {mobileOpen && (
        <div
          className="fixed inset-0 z-30 bg-black/50 md:hidden"
          onClick={closeMobileNav}
          aria-hidden="true"
        />
      )}

      <aside
        className={cn(
          'fixed inset-y-0 left-0 z-40 flex w-64 flex-col border-r border-border bg-card transition-transform duration-200 ease-in-out md:static md:w-64 md:shrink-0 md:translate-x-0',
          mobileOpen ? 'translate-x-0' : '-translate-x-full',
        )}
      >
        <div className="flex h-14 items-center border-b border-border px-4">
          <h1 className="text-lg font-semibold">Commerce</h1>
        </div>
        <nav aria-label="Primary" className="flex flex-1 flex-col gap-1 overflow-y-auto p-3">
          <NavItem to="/app/catalog" onNavigate={closeMobileNav}>Catalog</NavItem>
          <NavItem to="/app/orders" onNavigate={closeMobileNav}>Orders</NavItem>
          {/* commerce-customer-identity "Web admin gating": hidden, not just
              unreachable — a UX affordance, not the security boundary. The
              server's ManageUsers check on every /customers call is that. */}
          {hasPermission(user, Permission.ManageUsers) && (
            <>
              <NavItem to="/app/customers" onNavigate={closeMobileNav}>Customers</NavItem>
              <NavItem to="/app/users" onNavigate={closeMobileNav}>Users</NavItem>
              <NavItem to="/app/branches" onNavigate={closeMobileNav}>Branches</NavItem>
            </>
          )}
          {user?.isSystemAdmin && (
            <NavItem to="/app/organizations" onNavigate={closeMobileNav}>Organizations</NavItem>
          )}
        </nav>
      </aside>

      <div className="flex min-h-screen w-full min-w-0 flex-1 flex-col">
        <header className="flex h-14 items-center justify-between border-b border-border bg-card px-4 md:px-6">
          <button
            type="button"
            aria-label="Toggle navigation"
            onClick={() => setMobileOpen((prev) => !prev)}
            className="rounded-md p-1.5 text-foreground transition-colors hover:bg-accent hover:text-accent-foreground md:hidden"
          >
            <MenuIcon />
          </button>
          <div className="flex flex-1 items-center justify-end">
            <AccountMenu />
          </div>
        </header>
        <main className="w-full flex-1 p-4 md:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

function NavItem({ to, children, onNavigate }: { to: string; children: ReactNode; onNavigate?: () => void }) {
  return (
    <NavLink
      to={to}
      onClick={onNavigate}
      className={({ isActive }) =>
        cn(
          'flex items-center rounded-md px-3 py-2 text-sm font-medium transition-colors',
          isActive
            ? 'bg-accent text-accent-foreground'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
        )
      }
    >
      {children}
    </NavLink>
  )
}
