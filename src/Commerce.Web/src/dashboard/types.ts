/** Dashboard view models. The screen and the `DashboardSource` port speak only these types. */

export type DashboardPeriod = 'today' | '7d' | '30d'

export const dashboardPeriods: readonly DashboardPeriod[] = ['today', '7d', '30d']

/** Organization/branch the figures are scoped to (`null` = the caller's own / all branches). */
export interface DashboardScope {
  organizationId: string | null
  branchId: string | null
}

export interface DashboardQuery {
  scope: DashboardScope
  period: DashboardPeriod
}

/**
 * Sales channels are disjoint: `counter` is a POS sale handed over at the
 * shop, `delivery` is a POS sale dispatched by reparto, `web` is an online
 * order. A POS order is therefore `counter + delivery`.
 */
export type SalesChannel = 'counter' | 'delivery' | 'web'

export interface OrdersSummary {
  count: number
  /** ARS, whole pesos. */
  total: number
}

export interface DashboardKpis {
  posOrders: OrdersSummary
  webOrders: OrdersSummary
  /** `posOrders.total + webOrders.total`. */
  grandTotal: number
  moneyOnHand: number
  receivables: { total: number; accounts: number }
  /** POS sales dispatched by reparto. */
  deliverySales: number
  /** POS sales handed over at the counter. */
  counterSales: number
}

export interface SalesPoint {
  /** `YYYY-MM-DD` for daily buckets, `YYYY-MM-DDTHH:00` for hourly ones. */
  bucket: string
  counter: number
  delivery: number
  web: number
}

export interface SalesSeries {
  granularity: 'hour' | 'day'
  points: SalesPoint[]
}

export interface TopProduct {
  id: string
  name: string
  unit: 'kg' | 'u'
  quantity: number
  revenue: number
}

export interface StockRiskItem {
  id: string
  name: string
  unit: 'kg' | 'u'
  currentStock: number
  minimumStock: number
}

export interface CurrentAccount {
  id: string
  customer: string
  /** ARS owed by the customer. */
  balance: number
  daysOverdue: number
}

export interface DashboardData {
  period: DashboardPeriod
  kpis: DashboardKpis
  series: SalesSeries
  topProducts: TopProduct[]
  stockRisk: StockRiskItem[]
  currentAccounts: CurrentAccount[]
}
