import type { ReactNode } from 'react'
import { NavLink, Outlet } from 'react-router'
import { Button, buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { hasPermission, useAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'

/**
 * Authenticated shell chrome — moved from `App.tsx`'s `AuthenticatedApp`
 * (design.md "Route tree"). The `tab` state is replaced by `<NavLink>` +
 * `<Outlet/>` so each tab is a real, bookmarkable URL under `/app`.
 */
export function AppLayout() {
  const { user, signOut } = useAuth()

  return (
    <div className="mx-auto max-w-3xl p-6">
      <header className="mb-6 flex items-center justify-between">
        <h1 className="text-xl font-semibold">Commerce</h1>
        <div className="flex items-center gap-3">
          <span className="text-sm text-neutral-500">{user!.displayName}</span>
          <Button variant="outline" size="sm" onClick={() => void signOut()}>
            Sign out
          </Button>
        </div>
      </header>
      <nav className="mb-6 flex gap-2">
        <NavTab to="/app/catalog">Catalog</NavTab>
        <NavTab to="/app/orders">Orders</NavTab>
        {/* commerce-customer-identity "Web admin gating": hidden, not just
            unreachable — a UX affordance, not the security boundary. The
            server's ManageUsers check on every /customers call is that. */}
        {hasPermission(user, Permission.ManageUsers) && <>
          <NavTab to="/app/customers">Customers</NavTab>
          <NavTab to="/app/users">Users</NavTab>
          <NavTab to="/app/branches">Branches</NavTab>
        </>}
        {user?.isSystemAdmin && <NavTab to="/app/organizations">Organizations</NavTab>}
        <NavTab to="/app/password">Change password</NavTab>
      </nav>
      <Outlet />
    </div>
  )
}

function NavTab({ to, children }: { to: string; children: ReactNode }) {
  return (
    <NavLink
      to={to}
      className={({ isActive }) =>
        cn(buttonVariants({ variant: isActive ? 'default' : 'outline', size: 'sm' }))
      }
    >
      {children}
    </NavLink>
  )
}
