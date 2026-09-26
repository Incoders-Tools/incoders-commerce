import { useState, type ComponentType, type ReactNode } from 'react'
import { NavLink, Outlet } from 'react-router'
import { useTranslation } from 'react-i18next'
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
import { BranchSwitcher } from '@/components/layout/BranchSwitcher'
import { OrganizationSwitcher } from '@/components/layout/OrganizationSwitcher'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { useOrganizationBranding } from '@/theme/OrganizationBrandingProvider'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

/**
 * Enterprise app shell (T3, restructured for B7 U3): a full-width top bar
 * (brand, then — for a sysadmin — the organization switcher, then the
 * branch switcher, then the account menu) above a fixed sidebar nav on
 * desktop / hamburger-toggled off-canvas sidebar on mobile, and a full-width
 * content area (no more `max-w-3xl` centered column — user feedback was the
 * app "doesn't use the full screen"). "Change password" moved out of the
 * flat nav bar into `AccountMenu`; the theme switcher moved there too (was
 * mounted directly in the header as a T2 placeholder).
 */
export function AppLayout() {
  const { t } = useTranslation('nav')
  const { user } = useAuth()
  const organizationContext = useOptionalOrganizationContext()
  const branchContext = useOptionalBranchContext()
  const [mobileOpen, setMobileOpen] = useState(false)

  const closeMobileNav = () => setMobileOpen(false)

  // "On branch switch, screens must refetch" (tasks.md B7 U3): keying the
  // routed Outlet by organization+branch remounts every screen under it on
  // either change, instead of relying on each screen's own fetch effect to
  // notice the header changed. `'own'`/`'none'` are the same sentinels
  // `BranchContext`'s storage key uses, so "no selection" is still a stable,
  // distinct key rather than colliding with a real id.
  const outletKey = `${organizationContext?.selectedOrganization?.id ?? 'own'}:${branchContext?.selectedBranch?.id ?? 'none'}`

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
    <div className="flex min-h-screen flex-col bg-background text-foreground">
      <header className="flex h-14 items-center gap-3 border-b border-border bg-card px-4 md:px-6">
        <button
          type="button"
          aria-label={t('toggleNavigation')}
          onClick={() => setMobileOpen((prev) => !prev)}
          className="rounded-md p-1.5 text-foreground transition-colors hover:bg-accent hover:text-accent-foreground md:hidden"
        >
          <Menu aria-hidden="true" className="size-5 shrink-0" />
        </button>
        <div className="flex shrink-0 items-center">
          <BrandMark />
        </div>
        <div className="flex flex-1 items-center gap-3 overflow-x-auto">
          <OrganizationSwitcher />
          <BranchSwitcher />
        </div>
        <AccountMenu />
      </header>

      <div className="flex flex-1">
        {mobileOpen && (
          <div
            className="fixed inset-0 z-30 bg-black/50 md:hidden"
            onClick={closeMobileNav}
            aria-hidden="true"
          />
        )}

        <aside
          className={cn(
            'fixed inset-y-14 left-0 z-40 flex w-64 flex-col border-r border-border bg-card transition-transform duration-200 ease-in-out md:static md:w-64 md:shrink-0 md:translate-x-0',
            mobileOpen ? 'translate-x-0' : '-translate-x-full',
          )}
        >
          <nav aria-label={t('primary')} className="flex flex-1 flex-col gap-1 overflow-y-auto p-3">
            {showCatalogAndOrdersNav && (
              <NavSection title={t('sections.operations')}>
                <NavItem to="/app/catalog" icon={Package} onNavigate={closeMobileNav}>{t('items.catalog')}</NavItem>
                <NavItem to="/app/orders" icon={ClipboardList} onNavigate={closeMobileNav}>{t('items.orders')}</NavItem>
              </NavSection>
            )}
            {/* commerce-customer-identity "Web admin gating": hidden, not just
                unreachable — a UX affordance, not the security boundary. The
                server's ManageUsers check on every /customers call is that. */}
            {showTenantNav && (
              <NavSection title={t('sections.administration')}>
                <NavItem to="/app/customers" icon={Users2} onNavigate={closeMobileNav}>{t('items.customers')}</NavItem>
                <NavItem to="/app/users" icon={UserCog} onNavigate={closeMobileNav}>{t('items.users')}</NavItem>
                <NavItem to="/app/branches" icon={Store} onNavigate={closeMobileNav}>{t('items.branches')}</NavItem>
                {/* Same UI-only gate as its siblings: `App.tsx`'s
                    `RequireAdmin` is the routing boundary, and Pricing.cs's
                    own permission check is the real one. */}
                <NavItem to="/app/price-lists" icon={Tags} onNavigate={closeMobileNav}>{t('items.priceLists')}</NavItem>
              </NavSection>
            )}
            {user?.isSystemAdmin && (
              <NavSection title={t('sections.platform')}>
                <NavItem to="/app/organizations" icon={Building2} onNavigate={closeMobileNav}>{t('items.organizations')}</NavItem>
              </NavSection>
            )}
          </nav>
        </aside>

        <main className="w-full min-w-0 flex-1 p-4 md:p-6">
          <Outlet key={outletKey} />
        </main>
      </div>
    </div>
  )
}

/**
 * Small section heading grouping the sidebar's nav items (B7 U3, "more
 * attractive menu"): purely visual — every existing link keeps its exact
 * accessible name and stays a direct descendant of the single `<nav>`
 * landmark, so `AppLayout.test.tsx`'s `within(getByRole('navigation'))`
 * queries are unaffected.
 */
function NavSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1 pt-4 first:pt-0">
      <p className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</p>
      {children}
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
  const { t } = useTranslation('common')
  const { branding } = useOrganizationBranding()
  const [logoFailed, setLogoFailed] = useState(false)
  const logoUrl = branding?.logoUrl

  if (logoUrl && !logoFailed) {
    return (
      <img
        src={logoUrl}
        alt={t('app.logoAlt')}
        className="h-8 max-w-full object-contain"
        onError={() => setLogoFailed(true)}
      />
    )
  }

  return <h1 className="text-lg font-semibold">{t('app.name')}</h1>
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
