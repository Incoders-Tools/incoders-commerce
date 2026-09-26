import { Navigate, Route, Routes } from 'react-router'
import { AuthProvider, useOptionalAuth } from '@/auth/AuthContext'
import { OrganizationProvider, useOptionalOrganizationContext } from '@/organization/OrganizationContext'
import { OrganizationBrandingProvider } from '@/theme/OrganizationBrandingProvider'
import { ThemeProvider } from '@/theme/ThemeProvider'
import { AppLayout } from '@/routes/AppLayout'
import { RequireAuth } from '@/routes/RequireAuth'
import { RequireAdmin } from '@/routes/RequireAdmin'
import { RequireSystemAdmin } from '@/routes/RequireSystemAdmin'
import { LoginRoute } from '@/routes/LoginRoute'
import { ForgotPasswordRoute } from '@/routes/ForgotPasswordRoute'
import { ResetPasswordRoute } from '@/routes/ResetPasswordRoute'
import { HomeScreen } from '@/screens/HomeScreen'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { StaffOrderScreen } from '@/screens/StaffOrderScreen'
import { RenewPasswordScreen } from '@/screens/RenewPasswordScreen'
import { CustomersScreen } from '@/screens/CustomersScreen'
import { UsersScreen } from '@/screens/UsersScreen'
import { BranchesScreen } from '@/screens/BranchesScreen'
import { PriceListsScreen } from '@/screens/PriceListsScreen'
import { OrganizationsScreen } from '@/screens/OrganizationsScreen'

/**
 * `/app` landing. A system administrator with no real org permissions and no
 * selected organization only has platform screens (platform-administration
 * spec, "Sysadmin Acts On A Selected Organization"), so the catalog would be
 * a hidden, 403-answering page for them; everyone else keeps the catalog.
 */
function AppIndexRedirect() {
  const user = useOptionalAuth()?.user
  const selectedOrganization = useOptionalOrganizationContext()?.selectedOrganization
  const platformOnly = Boolean(user?.isSystemAdmin) && user?.permissions === 0 && selectedOrganization == null
  return <Navigate to={platformOnly ? 'organizations' : 'catalog'} replace />
}

/**
 * Route tree replacing the former auth ternary (design.md "Route tree").
 * Public routes (`/`, `/login`, `/forgot-password`, `/reset-password/...`)
 * render with zero dependency on auth machinery. Guarded staff routes live
 * under `/app`, behind `RequireAuth`.
 */
function App() {
  return (
    <AuthProvider>
      {/* Inside AuthProvider so ThemeProvider can read the signed-in user
          (useOptionalAuth) for per-user localStorage keying, while staying
          mounted for public routes too, where it falls back to an
          anonymous key. OrganizationProvider (platform-administration spec,
          "Sysadmin Acts On A Selected Organization") sits outermost of the
          three so `apiFetch` reflects the sysadmin's selected organization
          on every request, including the branding/theme fetches below it.
          OrganizationBrandingProvider also reads the signed-in user to
          fetch/clear the org's branding, and ThemeProvider consumes its
          result for the "custom" theme's colors. */}
      <OrganizationProvider>
        <OrganizationBrandingProvider>
          <ThemeProvider>
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
                  <Route index element={<AppIndexRedirect />} />
                  <Route path="catalog" element={<CatalogScreen />} />
                  {/* tasks.md 6.8 regression guard: unchanged staff-operated
                      submission path, extracted to its own component. */}
                  <Route path="orders" element={<StaffOrderScreen />} />
                  <Route path="password" element={<RenewPasswordScreen />} />
                  <Route element={<RequireAdmin />}>
                    <Route path="customers" element={<CustomersScreen />} />
                    <Route path="users" element={<UsersScreen />} />
                    <Route path="branches" element={<BranchesScreen />} />
                    {/* commerce-pricing-engine design.md "Web: `PriceListsScreen`
                        under the existing `RequireAdmin`". The screen existed
                        since Work Unit 9 but was never mounted here, which left
                        `price-list-management` ("Admin Create, Edit, and History
                        Access") and `supplier-price-import` ("Staged Batch
                        Requires Admin Review Before Commit") without any reachable
                        surface. `src/App.test.tsx` guards the mount itself. */}
                    <Route path="price-lists" element={<PriceListsScreen />} />
                  </Route>
                  <Route element={<RequireSystemAdmin />}>
                    <Route path="organizations" element={<OrganizationsScreen />} />
                  </Route>
                </Route>
              </Route>
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </ThemeProvider>
        </OrganizationBrandingProvider>
      </OrganizationProvider>
    </AuthProvider>
  )
}

export default App
