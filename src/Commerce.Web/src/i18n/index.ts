import i18next from 'i18next'
import { initReactI18next } from 'react-i18next'
import commonEs from './locales/es/common.json'
import navEs from './locales/es/nav.json'
import authEs from './locales/es/auth.json'
import catalogEs from './locales/es/catalog.json'
import ordersEs from './locales/es/orders.json'
import customersEs from './locales/es/customers.json'
import usersEs from './locales/es/users.json'
import branchesEs from './locales/es/branches.json'
import priceListsEs from './locales/es/priceLists.json'
import organizationsEs from './locales/es/organizations.json'
import themeEs from './locales/es/theme.json'
import errorsEs from './locales/es/errors.json'
import commonEn from './locales/en/common.json'
import navEn from './locales/en/nav.json'
import authEn from './locales/en/auth.json'
import catalogEn from './locales/en/catalog.json'
import ordersEn from './locales/en/orders.json'
import customersEn from './locales/en/customers.json'
import usersEn from './locales/en/users.json'
import branchesEn from './locales/en/branches.json'
import priceListsEn from './locales/en/priceLists.json'
import organizationsEn from './locales/en/organizations.json'
import themeEn from './locales/en/theme.json'
import errorsEn from './locales/en/errors.json'

export const defaultNamespace = 'common'

export const namespaces = [
  'common',
  'nav',
  'auth',
  'catalog',
  'orders',
  'customers',
  'users',
  'branches',
  'priceLists',
  'organizations',
  'theme',
  'errors',
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
    orders: ordersEs,
    customers: customersEs,
    users: usersEs,
    branches: branchesEs,
    priceLists: priceListsEs,
    organizations: organizationsEs,
    theme: themeEs,
    errors: errorsEs,
  },
  en: {
    common: commonEn,
    nav: navEn,
    auth: authEn,
    catalog: catalogEn,
    orders: ordersEn,
    customers: customersEn,
    users: usersEn,
    branches: branchesEn,
    priceLists: priceListsEn,
    organizations: organizationsEn,
    theme: themeEn,
    errors: errorsEn,
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
