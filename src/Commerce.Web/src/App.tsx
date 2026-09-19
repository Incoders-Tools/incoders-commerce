import { Navigate, Route, Routes } from 'react-router'
import { AuthProvider } from '@/auth/AuthContext'
import { AppLayout } from '@/routes/AppLayout'
import { RequireAuth } from '@/routes/RequireAuth'
import { RequireAdmin } from '@/routes/RequireAdmin'
import { LoginRoute } from '@/routes/LoginRoute'
import { ForgotPasswordRoute } from '@/routes/ForgotPasswordRoute'
import { ResetPasswordRoute } from '@/routes/ResetPasswordRoute'
import { HomeScreen } from '@/screens/HomeScreen'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { StaffOrderScreen } from '@/screens/StaffOrderScreen'
import { RenewPasswordScreen } from '@/screens/RenewPasswordScreen'
import { CustomersScreen } from '@/screens/CustomersScreen'

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
        {/* commerce-guest-ordering design.md "One screen, guest and
            registered as peers": the platform's first public-reachable
            path — no RequireAuth, a guest has no staff or customer session
            yet. */}
        <Route path="/order" element={<OrderScreen />} />
        <Route element={<RequireAuth />}>
          <Route path="/app" element={<AppLayout />}>
            <Route index element={<Navigate to="catalog" replace />} />
            <Route path="catalog" element={<CatalogScreen />} />
            {/* tasks.md 6.8 regression guard: unchanged staff-operated
                submission path, extracted to its own component. */}
            <Route path="orders" element={<StaffOrderScreen />} />
            <Route path="password" element={<RenewPasswordScreen />} />
            <Route element={<RequireAdmin />}>
              <Route path="customers" element={<CustomersScreen />} />
            </Route>
          </Route>
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  )
}

export default App
