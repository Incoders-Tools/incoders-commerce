import { Navigate, Outlet, useLocation } from 'react-router'
import { useAuth } from '@/auth/AuthContext'

/**
 * Route guard (design.md "Guard implementation"). Redirects to `/login`
 * instead of blank-rendering, and defaults to denial: only a non-null
 * `user` reaches `<Outlet/>`. The originally-requested location travels in
 * in-memory `location.state`, never a `?returnTo=` query param (rejected as
 * an open-redirect surface).
 */
export function RequireAuth() {
  const { user } = useAuth()
  const location = useLocation()

  return user ? <Outlet /> : <Navigate to="/login" replace state={{ from: location }} />
}
