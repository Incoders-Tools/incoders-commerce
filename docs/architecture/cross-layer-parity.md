# Cross-layer parity: web app and POS desktop

The product has two staff-facing layers over ONE server:

- **Web** (`src/Commerce.Web`): the back office: administration, catalog, prices, orders, treasury, staff, reports.
- **POS desktop** (`src/Commerce.Pos.Windows` + `src/Commerce.BranchNode`): the counter of a branch. It sells offline from
  a local replica and manages customers and staff online, through the SAME Cloud API endpoints the web uses
  (`ManagementConnection`: device credential + signed-in operator, endpoints opted in with `AllowDeviceOperator()`).

Rule (skill `skills/cross-layer-parity`): every change to something present in both layers is checked in the other one.
This map says where each shared feature lives and what differs on purpose. Update it in the same change.

Status: **Aligned** (same operations, same wording), **Partial** (shared core, listed gaps), **One layer** (by design).

## Shared features

| Feature | Web | POS | Status | Notes |
|---|---|---|---|---|
| Customers: list, search, create, edit, contacts | `screens/CustomersScreen.tsx`, `CustomerForm.tsx`, `ContactsEditor.tsx` | `CustomersView.xaml(.cs)`, `CustomerList.cs`, `CustomerAdminClient.cs` | Aligned | Same `/customers` endpoints. Same list columns in the same order: Nombre, Tipo (Persona/Empresa), CUIT/DNI, Teléfono, Ubicación ("Ciudad — Provincia"), Tipo de negocio, Lista de precios, Estado; the web adds Saldo (accounting). Both forms edit the business type (`/customers/business-types`, read by the POS). Intentional differences: contacts, price list and payment terms are edited in the web only; the POS hides the secondary columns while its form is open. |
| Customer current account | `SupplierAccountScreen.tsx` (`CustomerAccountScreen`) | `CustomerPaymentWindow` (collect), sale tender "Cuenta corriente" | Partial | POS collects payments and sells on account; statement and manual movements are web only. |
| Staff (employees): list, create, edit, deactivate, advance, account | `EmployeesScreen.tsx`, `SupplierAccountScreen.tsx` (`EmployeeAccountScreen`) | `PersonalView.xaml(.cs)` tab "Empleados": `EmployeesView.xaml(.cs)`, `EmployeeList.cs`, `EmployeeAdminClient.cs` | Aligned | Same `/employees`, `/employees/roles` and `/treasury/accounts` endpoints; same columns, texts and input rules (`EmployeeFormRules` mirrors the web's `parseAmount`). Intentional differences: the POS lists and creates staff in its own branch only (the web picks the branch, and an edit at the POS keeps the stored one); the POS reads the account statement, while manual movements and reversals stay web only (accounting). Tests: `PosEmployeesTests`, `DeviceOperatorManagementTests`. |
| System users: create, role, deactivate, reset password | `UsersScreen.tsx` (Sistema → Usuarios) | `PersonalView` tab "Usuarios y acceso": `StaffView.xaml(.cs)`, `UserAdminClient.cs` | Aligned | Same `/account/users` endpoints. |
| POS operators of a terminal (PIN sign-in) | — | `StaffView` "Operadores de esta terminal" | One layer | Local to each terminal by nature. |
| Staff positions ("Puestos") | `EmployeeRolesScreen.tsx` (Tablas auxiliares) | read in the employee form | Partial | Catalog ABM in web; the POS picks from it. |
| Categories shown on the POS rail | `CategoriesScreen.tsx` ("Mostrar en el POS", "Orden") | `Controls/CategoryRail`, `BranchSyncStore.Categories.cs` | Aligned | Configured on the web, synced with the `price-lists` snapshot. |
| Prices and price lists | `PriceListsScreen.tsx`, `PriceEditorTab.tsx` | read-only replica (`BuyerPricing.cs`) | One layer | Edited on the web; the POS prices from the replica. |
| Catalog (products, codes) | `CatalogScreen.tsx`, `CatalogProductForm.tsx` | read-only replica, product cards | One layer | Edited on the web. |
| Selling | Order taking: `StaffOrderScreen.tsx`, `OrdersScreen.tsx` | Counter sale: `MainWindow`, `SaleCart.cs`, `TenderWindow` | Intentional difference | A web order is fulfilled and delivered; a POS sale is paid at the counter. Shared parts (prices, customer list price and discount, rounding, product names) must match. |
| Sales history and voids | Orders tracking (`OrdersScreen`, `OrderTrackingScreen`) | `SalesView.xaml` (sales, payments, cash movements; void with PIN) | Intentional difference | Different documents (order vs sale). |
| Cash sessions, drawer movements, cash count | Treasury shows their effect (`TreasuryScreen.tsx`) | `OpenCashWindow`, `CashMovementWindow`, `CloseCashWindow` | Intentional difference | The drawer is operated at the POS; the web administers the money. |
| Treasury: accounts, manual and recurring movements, transfers | `TreasuryScreen.tsx`, `TreasuryForms.tsx`, `TreasuryRecurrences.tsx` | — | One layer | Administration. |
| Payroll | `PayrollScreen.tsx`, `PayrollRunScreen.tsx`, `PayslipsPrintScreen.tsx` | — | One layer | Administration; staff goods are sold at the POS to the employee's linked customer. |
| Organization account standing (payment to the platform) | `components/layout/AccountStanding.tsx` (banner, suspended screen), `routes/AppLayout.tsx`; sysadmin: `screens/OrganizationStandingForm.tsx` | footer pill `AccountStandingPill` in `MainWindow.xaml(.cs)`, `AccountStandingNotice.cs`, `BranchSyncStore.AccountStanding.cs` | Intentional difference | One rule for both: `AccountStandingRules` (`Commerce.Domain`); the cloud evaluates it for the web, the POS evaluates the synced inputs (`/device/organization/settings`) on its own business day, so it keeps counting offline. "Admin" means `ManageUsers` in both. Intentional differences (owner decisions, `odd/tasks/organization-account-standing.md`): the web warns admins only and blocks every user once Suspended (403 `organization-suspended`); the POS warns EVERY signed-in operator in red and never blocks (ADR-002), so its text says "acceso web" and closes with "Informe al administrador." (cashier) or "Comuníquese con Incoders." (admin). Tests: `OrganizationSuspensionEnforcementTests`, `PosAccountStandingTests`, vitest `AppLayout`/`OrganizationStandingForm`. |

## Shared conventions

- **Location**: a place is shown as "Ciudad — Provincia" under the header "Ubicación" (customers, suppliers), never "Ciudad".
- **Row actions (web)**: up to two actions are buttons; with three or more the main one stays a button and the rest go to
  the "…" menu (`components/data/RowActions.tsx`). The POS rows use icon buttons (`EntityListView`).

## Shared wording

Statuses, labels and errors read the same in both layers. Spanish source of truth: the web i18n files
(`src/Commerce.Web/src/i18n/locales/es/*.json`); the POS literals in XAML/C# follow them (e.g. "Le debemos" / "Nos debe"
on an employee's account, "Dar de baja" / "Reincorporar", "Adelanto").
