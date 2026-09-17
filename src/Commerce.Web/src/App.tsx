import { Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from '@/auth/AuthContext'
import { AppLayout } from '@/routes/AppLayout'
import { RequireAuth } from '@/routes/RequireAuth'
import { LoginRoute } from '@/routes/LoginRoute'
import { ForgotPasswordRoute } from '@/routes/ForgotPasswordRoute'
import { ResetPasswordRoute } from '@/routes/ResetPasswordRoute'
import { HomeScreen } from '@/screens/HomeScreen'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { RenewPasswordScreen } from '@/screens/RenewPasswordScreen'

/**
 * Route tree replacing the former auth ternary (design.md "Route tree").
 * Public routes (`/`, `/login`, `/forgot-password`, `/reset-password/...`)
 * render with zero dependency on auth machinery. Guarded staff routes live
 * under `/app`, behind `RequireAuth`.
 */
function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/" element={<HomeScreen />} />
        <Route path="/login" element={<LoginRoute />} />
        <Route path="/forgot-password" element={<ForgotPasswordRoute />} />
        <Route path="/reset-password/:token" element={<ResetPasswordRoute />} />
        <Route path="/reset-password" element={<ResetPasswordRoute />} />
        <Route element={<RequireAuth />}>
          <Route path="/app" element={<AppLayout />}>
            <Route index element={<Navigate to="catalog" replace />} />
            <Route path="catalog" element={<CatalogScreen />} />
            <Route path="orders" element={<OrderScreen />} />
            <Route path="password" element={<RenewPasswordScreen />} />
          </Route>
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  )
}

export default App
