import { useState, type ComponentType, type ReactNode } from 'react'
import { NavLink, Outlet } from 'react-router'
import {
  Building2,
  ClipboardList,
  Menu,
  Package,
  Store,
  Tags,
  UserCog,
  Users2,
  type LucideProps,
} from 'lucide-react'
import { cn } from '@/lib/utils'
import { hasPermission, useAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import { AccountMenu } from '@/components/layout/AccountMenu'
import { OrganizationSwitcher } from '@/components/layout/OrganizationSwitcher'
import { useOrganizationBranding } from '@/theme/OrganizationBrandingProvider'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

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
  const organizationContext = useOptionalOrganizationContext()
  const [mobileOpen, setMobileOpen] = useState(false)

  const closeMobileNav = () => setMobileOpen(false)

  // platform-administration spec, "Sysadmin Acts On A Selected
  // Organization": a system administrator holds zero `Permission` bits on
  // their own (hidden) organization, so `hasPermission` alone would hide
  // every tenant module for them even after selecting a target
  // organization — the server elevates their effective permissions for
  // that request, and the nav mirrors it here. Without a selection, a
  // sysadmin sees ONLY Organizations and other platform-level screens.
  const actingAsSysadminOnSelectedOrganization =
    Boolean(user?.isSystemAdmin) && organizationContext?.selectedOrganization != null
  const showTenantNav = hasPermission(user, Permission.ManageUsers) || actingAsSysadminOnSelectedOrganization
  // A sysadmin with no real org-scoped Permission (the common case) sees
  // Catalog/Orders only once they have selected an organization; a sysadmin
  // who was ALSO separately granted real permissions (uncommon, but not
  // precluded by the model) is treated like any other permission holder.
  const sysadminWithNoRealPermissions = Boolean(user?.isSystemAdmin) && user?.permissions === 0
  const showCatalogAndOrdersNav = !sysadminWithNoRealPermissions || actingAsSysadminOnSelectedOrganization

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
          <BrandMark />
        </div>
        <nav aria-label="Primary" className="flex flex-1 flex-col gap-1 overflow-y-auto p-3">
          {showCatalogAndOrdersNav && (
            <>
              <NavItem to="/app/catalog" icon={Package} onNavigate={closeMobileNav}>Catalog</NavItem>
              <NavItem to="/app/orders" icon={ClipboardList} onNavigate={closeMobileNav}>Orders</NavItem>
            </>
          )}
          {/* commerce-customer-identity "Web admin gating": hidden, not just
              unreachable — a UX affordance, not the security boundary. The
              server's ManageUsers check on every /customers call is that. */}
          {showTenantNav && (
            <>
              <NavItem to="/app/customers" icon={Users2} onNavigate={closeMobileNav}>Customers</NavItem>
              <NavItem to="/app/users" icon={UserCog} onNavigate={closeMobileNav}>Users</NavItem>
              <NavItem to="/app/branches" icon={Store} onNavigate={closeMobileNav}>Branches</NavItem>
              {/* Same UI-only gate as its siblings: `App.tsx`'s
                  `RequireAdmin` is the routing boundary, and Pricing.cs's
                  own permission check is the real one. */}
              <NavItem to="/app/price-lists" icon={Tags} onNavigate={closeMobileNav}>Price lists</NavItem>
            </>
          )}
          {user?.isSystemAdmin && (
            <NavItem to="/app/organizations" icon={Building2} onNavigate={closeMobileNav}>Organizations</NavItem>
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
            <Menu aria-hidden="true" className="size-5 shrink-0" />
          </button>
          <div className="flex flex-1 items-center justify-end gap-3">
            <OrganizationSwitcher />
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

/**
 * T6: the sidebar brand spot. `SignedInResponse` carries no organization
 * name (only `displayName`/`organizationId`), so the logo's alt text is
 * always the generic "Organization logo" rather than a name we don't have.
 * No `logoUrl`, or a `logoUrl` that fails to load (broken link, blocked
 * origin), both fall back to the original text brand — never a broken
 * image icon.
 */
function BrandMark() {
  const { branding } = useOrganizationBranding()
  const [logoFailed, setLogoFailed] = useState(false)
  const logoUrl = branding?.logoUrl

  if (logoUrl && !logoFailed) {
    return (
      <img
        src={logoUrl}
        alt="Organization logo"
        className="h-8 max-w-full object-contain"
        onError={() => setLogoFailed(true)}
      />
    )
  }

  return <h1 className="text-lg font-semibold">Commerce</h1>
}

function NavItem({
  to,
  children,
  icon: Icon,
  onNavigate,
}: {
  to: string
  children: ReactNode
  icon: ComponentType<LucideProps>
  onNavigate?: () => void
}) {
  return (
    <NavLink
      to={to}
      onClick={onNavigate}
      className={({ isActive }) =>
        cn(
          'flex items-center gap-2.5 rounded-md px-3 py-2 text-sm font-medium transition-colors',
          isActive
            ? 'bg-accent text-accent-foreground'
            : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
        )
      }
    >
      <Icon aria-hidden="true" className="size-4 shrink-0" />
      {children}
    </NavLink>
  )
}
