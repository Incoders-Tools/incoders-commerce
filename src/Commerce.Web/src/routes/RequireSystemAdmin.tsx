import { Navigate, Outlet } from 'react-router'
import { useAuth } from '@/auth/AuthContext'

export function RequireSystemAdmin() {
  const { user } = useAuth()
  return user?.isSystemAdmin ? <Outlet /> : <Navigate to="/app/catalog" replace />
}
