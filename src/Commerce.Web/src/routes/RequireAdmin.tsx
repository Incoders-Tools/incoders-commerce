import { Navigate, Outlet } from 'react-router'
import { hasPermission, useAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'

/**
 * `RequireAuth`'s exact shape (design.md "Web admin gating"), additionally
 * requiring the `ManageUsers` bit. Redirects to `/app/catalog` — not
 * `/login` — because this guard only ever runs nested under `RequireAuth`,
 * so a denial here means "signed in, not an admin", not "not signed in".
 * Default-deny on a null `user` (e.g. this guard rendered standalone in a
 * test) mirrors `RequireAuth`'s own default-deny.
 *
 * The server's `ManageUsers` check on every `/customers` call remains the
 * authority — this guard only prevents a dead-end screen, it does not
 * protect the data.
 */
export function RequireAdmin() {
  const { user } = useAuth()

  return hasPermission(user, Permission.ManageUsers) ? (
    <Outlet />
  ) : (
    <Navigate to="/app/catalog" replace />
  )
}
