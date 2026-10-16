import i18next from 'i18next'
import { initReactI18next } from 'react-i18next'
import commonEs from './locales/es/common.json'
import navEs from './locales/es/nav.json'
import authEs from './locales/es/auth.json'
import catalogEs from './locales/es/catalog.json'
import categoriesEs from './locales/es/categories.json'
import citiesEs from './locales/es/cities.json'
import businessTypesEs from './locales/es/businessTypes.json'
import ordersEs from './locales/es/orders.json'
import customersEs from './locales/es/customers.json'
import suppliersEs from './locales/es/suppliers.json'
import supplierCategoriesEs from './locales/es/supplierCategories.json'
import treasuryAccountTypesEs from './locales/es/treasuryAccountTypes.json'
import employeesEs from './locales/es/employees.json'
import employeeRolesEs from './locales/es/employeeRoles.json'
import employeeAccountEs from './locales/es/employeeAccount.json'
import payrollEs from './locales/es/payroll.json'
import supplierAccountEs from './locales/es/supplierAccount.json'
import customerAccountEs from './locales/es/customerAccount.json'
import usersEs from './locales/es/users.json'
import branchesEs from './locales/es/branches.json'
import priceListsEs from './locales/es/priceLists.json'
import organizationsEs from './locales/es/organizations.json'
import themeEs from './locales/es/theme.json'
import errorsEs from './locales/es/errors.json'
import dashboardEs from './locales/es/dashboard.json'
import purchasesEs from './locales/es/purchases.json'
import stockEs from './locales/es/stock.json'
import fulfillmentEs from './locales/es/fulfillment.json'
import treasuryEs from './locales/es/treasury.json'
import commonEn from './locales/en/common.json'
import navEn from './locales/en/nav.json'
import authEn from './locales/en/auth.json'
import catalogEn from './locales/en/catalog.json'
import categoriesEn from './locales/en/categories.json'
import citiesEn from './locales/en/cities.json'
import businessTypesEn from './locales/en/businessTypes.json'
import ordersEn from './locales/en/orders.json'
import customersEn from './locales/en/customers.json'
import suppliersEn from './locales/en/suppliers.json'
import supplierCategoriesEn from './locales/en/supplierCategories.json'
import treasuryAccountTypesEn from './locales/en/treasuryAccountTypes.json'
import employeesEn from './locales/en/employees.json'
import employeeRolesEn from './locales/en/employeeRoles.json'
import employeeAccountEn from './locales/en/employeeAccount.json'
import payrollEn from './locales/en/payroll.json'
import supplierAccountEn from './locales/en/supplierAccount.json'
import customerAccountEn from './locales/en/customerAccount.json'
import usersEn from './locales/en/users.json'
import branchesEn from './locales/en/branches.json'
import priceListsEn from './locales/en/priceLists.json'
import organizationsEn from './locales/en/organizations.json'
import themeEn from './locales/en/theme.json'
import errorsEn from './locales/en/errors.json'
import dashboardEn from './locales/en/dashboard.json'
import purchasesEn from './locales/en/purchases.json'
import stockEn from './locales/en/stock.json'
import fulfillmentEn from './locales/en/fulfillment.json'
import treasuryEn from './locales/en/treasury.json'

export const defaultNamespace = 'common'

export const namespaces = [
  'common',
  'nav',
  'auth',
  'catalog',
  'categories',
  'cities',
  'businessTypes',
  'orders',
  'customers',
  'suppliers',
  'supplierCategories',
  'treasuryAccountTypes',
  'employees',
  'employeeRoles',
  'employeeAccount',
  'payroll',
  'supplierAccount',
  'customerAccount',
  'users',
  'branches',
  'priceLists',
  'organizations',
  'theme',
  'errors',
  'dashboard',
  'purchases',
  'stock',
  'fulfillment',
  'treasury',
] as const

// Owner decision (2026-09-25): clients and organizations are Spanish-speaking,
// so the app ships in Spanish now, with no browser-language auto-detection —
// keeping startup simple and predictable. `en` resources are bundled too (kept
// at full parity, see the i18n key-parity test) purely to prove the i18n seam
// end to end; nothing in the running app switches to it yet, so this stays a
// static resource bundle rather than pulling in i18next's detector/backend
// plugins.
export const resources = {
  es: {
    common: commonEs,
    nav: navEs,
    auth: authEs,
    catalog: catalogEs,
    categories: categoriesEs,
    cities: citiesEs,
    businessTypes: businessTypesEs,
    orders: ordersEs,
    customers: customersEs,
    suppliers: suppliersEs,
    supplierCategories: supplierCategoriesEs,
    treasuryAccountTypes: treasuryAccountTypesEs,
    employees: employeesEs,
    employeeRoles: employeeRolesEs,
    employeeAccount: employeeAccountEs,
    payroll: payrollEs,
    supplierAccount: supplierAccountEs,
    customerAccount: customerAccountEs,
    users: usersEs,
    branches: branchesEs,
    priceLists: priceListsEs,
    organizations: organizationsEs,
    theme: themeEs,
    errors: errorsEs,
    dashboard: dashboardEs,
    purchases: purchasesEs,
    stock: stockEs,
    fulfillment: fulfillmentEs,
    treasury: treasuryEs,
  },
  en: {
    common: commonEn,
    nav: navEn,
    auth: authEn,
    catalog: catalogEn,
    categories: categoriesEn,
    cities: citiesEn,
    businessTypes: businessTypesEn,
    orders: ordersEn,
    customers: customersEn,
    suppliers: suppliersEn,
    supplierCategories: supplierCategoriesEn,
    treasuryAccountTypes: treasuryAccountTypesEn,
    employees: employeesEn,
    employeeRoles: employeeRolesEn,
    employeeAccount: employeeAccountEn,
    payroll: payrollEn,
    supplierAccount: supplierAccountEn,
    customerAccount: customerAccountEn,
    users: usersEn,
    branches: branchesEn,
    priceLists: priceListsEn,
    organizations: organizationsEn,
    theme: themeEn,
    errors: errorsEn,
    dashboard: dashboardEn,
    purchases: purchasesEn,
    stock: stockEn,
    fulfillment: fulfillmentEn,
    treasury: treasuryEn,
  },
} as const

let initialized = false

export function initI18n() {
  if (initialized) return i18next
  initialized = true
  void i18next.use(initReactI18next).init({
    resources,
    lng: 'es',
    fallbackLng: 'es',
    defaultNS: defaultNamespace,
    ns: namespaces,
    interpolation: { escapeValue: false },
    returnNull: false,
  })
  return i18next
}

export default i18next
