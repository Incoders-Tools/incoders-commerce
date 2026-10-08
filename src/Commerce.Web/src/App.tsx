import { Navigate, Route, Routes } from 'react-router'
import { AuthProvider, hasPermission, useOptionalAuth } from '@/auth/AuthContext'
import { Permission } from '@/api/types'
import { OrganizationProvider, useOptionalOrganizationContext } from '@/organization/OrganizationContext'
import { BranchProvider } from '@/branch/BranchContext'
import { OrganizationBrandingProvider } from '@/theme/OrganizationBrandingProvider'
import { NumberFormatProvider } from '@/organization/NumberFormatContext'
import { OrganizationSettingsScreen } from '@/screens/OrganizationSettingsScreen'
import { ThemeProvider } from '@/theme/ThemeProvider'
import { AppLayout } from '@/routes/AppLayout'
import { RequireAuth } from '@/routes/RequireAuth'
import { RequireAdmin } from '@/routes/RequireAdmin'
import { RequireSystemAdmin } from '@/routes/RequireSystemAdmin'
import { RequireTakeOrders } from '@/routes/RequireTakeOrders'
import { canTakeOrders } from '@/auth/canTakeOrders'
import { LoginRoute } from '@/routes/LoginRoute'
import { ForgotPasswordRoute } from '@/routes/ForgotPasswordRoute'
import { ResetPasswordRoute } from '@/routes/ResetPasswordRoute'
import { HomeScreen } from '@/screens/HomeScreen'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { BusinessTypesScreen } from '@/screens/BusinessTypesScreen'
import { CategoriesScreen } from '@/screens/CategoriesScreen'
import { CitiesScreen } from '@/screens/CitiesScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { StaffOrderScreen } from '@/screens/StaffOrderScreen'
import { OrdersScreen } from '@/screens/OrdersScreen'
import { OrderTrackingScreen } from '@/screens/OrderTrackingScreen'
import { DeliveriesScreen } from '@/screens/DeliveriesScreen'
import { DeliveryRunFormScreen } from '@/screens/DeliveryRunFormScreen'
import { DeliveryRunScreen } from '@/screens/DeliveryRunScreen'
import { DeliveryRunSettleScreen } from '@/screens/DeliveryRunSettleScreen'
import { RemitoPrintScreen } from '@/screens/RemitoPrintScreen'
import { RenewPasswordScreen } from '@/screens/RenewPasswordScreen'
import { CustomersScreen } from '@/screens/CustomersScreen'
import { SuppliersScreen } from '@/screens/SuppliersScreen'
import { CustomerAccountScreen, SupplierAccountScreen } from '@/screens/SupplierAccountScreen'
import { SupplierCategoriesScreen } from '@/screens/SupplierCategoriesScreen'
import { TreasuryAccountTypesScreen } from '@/screens/TreasuryAccountTypesScreen'
import { EmployeesScreen } from '@/screens/EmployeesScreen'
import { EmployeeRolesScreen } from '@/screens/EmployeeRolesScreen'
import { EmployeeAccountScreen } from '@/screens/SupplierAccountScreen'
import { PayrollScreen } from '@/screens/PayrollScreen'
import { PayrollRunScreen } from '@/screens/PayrollRunScreen'
import { PayslipsPrintScreen } from '@/screens/PayslipsPrintScreen'
import { ReceptionsScreen } from '@/screens/ReceptionsScreen'
import { ReceptionScreen } from '@/screens/ReceptionScreen'
import { StockScreen } from '@/screens/StockScreen'
import { StockMovementsScreen } from '@/screens/StockMovementsScreen'
import { UsersScreen } from '@/screens/UsersScreen'
import { BranchesScreen } from '@/screens/BranchesScreen'
import { PriceListsScreen } from '@/screens/PriceListsScreen'
import { OrganizationsScreen } from '@/screens/OrganizationsScreen'
import { DashboardScreen } from '@/screens/DashboardScreen'
import { TreasuryScreen } from '@/screens/TreasuryScreen'

/**
 * `/app` landing. A system administrator with no real org permissions and no
 * selected organization only has platform screens (platform-administration
 * spec, "Sysadmin Acts On A Selected Organization"), so every tenant screen
 * would be a hidden, 403-answering page for them: they land on Organizations.
 * A business admin (or a sysadmin acting on an organization) lands on the
 * dashboard, the same population `RequireAdmin` lets in. A seller (may take
 * orders, no administration) lands on the orders of the branch, their main area
 * (taking a new one is one click away);
 * everyone else keeps the catalog.
 */
function AppIndexRedirect() {
  const user = useOptionalAuth()?.user ?? null
  const selectedOrganization = useOptionalOrganizationContext()?.selectedOrganization
  const platformOnly = Boolean(user?.isSystemAdmin) && user?.permissions === 0 && selectedOrganization == null
  const isAdmin =
    hasPermission(user, Permission.ManageUsers) || (Boolean(user?.isSystemAdmin) && selectedOrganization != null)
  const target = platformOnly
    ? 'organizations'
    : isAdmin
      ? 'dashboard'
      : canTakeOrders(user, selectedOrganization != null)
        ? 'orders'
        : 'catalog'
  return <Navigate to={target} replace />
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
          result for the "custom" theme's colors. BranchProvider
          (admin-console spec, "Top Navbar Branch Switcher") sits right
          inside OrganizationProvider: it reads the sysadmin's selected
          organization, and `apiFetch` must carry `X-Branch-Id` on every
          request below it, or branch-owned endpoints answer 400
          `branch-selection-required`. */}
      <OrganizationProvider>
        <BranchProvider>
        <OrganizationBrandingProvider>
          <NumberFormatProvider>
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
                {/* Printable remitos: outside the app shell so only the documents print. */}
                <Route element={<RequireTakeOrders />}>
                  <Route path="/print/remitos" element={<RemitoPrintScreen />} />
                </Route>
                {/* Payslip receipts of a paid payroll: outside the app shell, administration only. */}
                <Route element={<RequireAdmin />}>
                  <Route path="/print/payslips/:id" element={<PayslipsPrintScreen />} />
                </Route>
                <Route path="/app" element={<AppLayout />}>
                  <Route index element={<AppIndexRedirect />} />
                  <Route path="catalog" element={<CatalogScreen />} />
                  {/* staff-order-taking + order-fulfillment-and-delivery: the orders of the branch (tracking list,
                      one order, taking a new one) and its delivery runs. Not a redirect guard: without TakeOrders
                      it shows a "no access" state. */}
                  <Route element={<RequireTakeOrders />}>
                    <Route path="orders" element={<OrdersScreen />} />
                    <Route path="orders/new" element={<StaffOrderScreen />} />
                    <Route path="orders/:id" element={<OrderTrackingScreen />} />
                    <Route path="deliveries" element={<DeliveriesScreen />} />
                    <Route path="deliveries/new" element={<DeliveryRunFormScreen />} />
                    <Route path="deliveries/:id" element={<DeliveryRunScreen />} />
                    <Route path="deliveries/:id/edit" element={<DeliveryRunFormScreen />} />
                    <Route path="deliveries/:id/settle" element={<DeliveryRunSettleScreen />} />
                  </Route>
                  <Route path="password" element={<RenewPasswordScreen />} />
                  <Route element={<RequireAdmin />}>
                    <Route path="dashboard" element={<DashboardScreen />} />
                    <Route path="customers" element={<CustomersScreen />} />
                    <Route path="customers/:id/account" element={<CustomerAccountScreen />} />
                    <Route path="treasury" element={<TreasuryScreen />} />
                    <Route path="business-types" element={<BusinessTypesScreen />} />
                    <Route path="suppliers" element={<SuppliersScreen />} />
                    <Route path="suppliers/:id/account" element={<SupplierAccountScreen />} />
                    <Route path="supplier-categories" element={<SupplierCategoriesScreen />} />
                    <Route path="treasury-account-types" element={<TreasuryAccountTypesScreen />} />
                    <Route path="employees" element={<EmployeesScreen />} />
                    <Route path="employees/:id/account" element={<EmployeeAccountScreen />} />
                    <Route path="employee-roles" element={<EmployeeRolesScreen />} />
                    <Route path="payroll" element={<PayrollScreen />} />
                    <Route path="payroll/:id" element={<PayrollRunScreen />} />
                    <Route path="receptions" element={<ReceptionsScreen />} />
                    <Route path="receptions/new" element={<ReceptionScreen />} />
                    <Route path="receptions/:id" element={<ReceptionScreen />} />
                    <Route path="stock" element={<StockScreen />} />
                    <Route path="stock/:presentationId/movements" element={<StockMovementsScreen />} />
                    <Route path="users" element={<UsersScreen />} />
                    <Route path="branches" element={<BranchesScreen />} />
                    <Route path="settings" element={<OrganizationSettingsScreen />} />
                    {/* commerce-pricing-engine design.md "Web: `PriceListsScreen`
                        under the existing `RequireAdmin`". The screen existed
                        since Work Unit 9 but was never mounted here, which left
                        `price-list-management` ("Admin Create, Edit, and History
                        Access") and `supplier-price-import` ("Staged Batch
                        Requires Admin Review Before Commit") without any reachable
                        surface. `src/App.test.tsx` guards the mount itself. */}
                    <Route path="price-lists" element={<PriceListsScreen />} />
                    <Route path="categories" element={<CategoriesScreen />} />
                  </Route>
                  <Route element={<RequireSystemAdmin />}>
                    <Route path="organizations" element={<OrganizationsScreen />} />
                    {/* Core geography (Georef cities): shared by every organization, so it
                        belongs to the platform area, not to a tenant's administration. */}
                    <Route path="cities" element={<CitiesScreen />} />
                  </Route>
                </Route>
              </Route>
              <Route path="*" element={<Navigate to="/" replace />} />
            </Routes>
          </ThemeProvider>
          </NumberFormatProvider>
        </OrganizationBrandingProvider>
        </BranchProvider>
      </OrganizationProvider>
    </AuthProvider>
  )
}

export default App
