import { Navigate, Outlet } from 'react-router'
import { hasPermission, useAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

/**
 * `RequireAuth`'s exact shape (design.md "Web admin gating"), additionally
 * requiring the `ManageUsers` bit. Redirects to `/app/catalog` — not
 * `/login` — because this guard only ever runs nested under `RequireAuth`,
 * so a denial here means "signed in, not an admin", not "not signed in".
 * Default-deny on a null `user` (e.g. this guard rendered standalone in a
 * test) mirrors `RequireAuth`'s own default-deny.
 *
 * platform-administration spec, "Sysadmin Acts On A Selected Organization":
 * a system administrator holds NO `Permission` bits on their own (hidden)
 * organization, so `hasPermission` alone would wrongly deny them here once
 * they select a target organization — the server elevates their effective
 * permissions for that request, and this guard mirrors that: a sysadmin
 * with a selected organization passes exactly like a `business-admin`
 * would. A sysadmin who has NOT selected an organization is still denied,
 * matching the server ("no tenant module data").
 *
 * The server's `ManageUsers` check (elevated or not) on every `/customers`
 * call remains the authority — this guard only prevents a dead-end screen,
 * it does not protect the data.
 */
export function RequireAdmin() {
  const { user } = useAuth()
  const organizationContext = useOptionalOrganizationContext()
  const actingAsSysadminOnSelectedOrganization =
    Boolean(user?.isSystemAdmin) && organizationContext?.selectedOrganization != null

  return hasPermission(user, Permission.ManageUsers) || actingAsSysadminOnSelectedOrganization ? (
    <Outlet />
  ) : (
    <Navigate to="/app/catalog" replace />
  )
}
