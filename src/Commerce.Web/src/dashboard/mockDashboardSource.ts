// TEMPORARY MOCK — remove when the owner starts real data; see odd/tasks/business-dashboard.md
import type { DashboardSource } from './port'
import type {
  CurrentAccount,
  DashboardData,
  DashboardPeriod,
  DashboardQuery,
  SalesPoint,
  StockRiskItem,
  TopProduct,
} from './types'

/** Small seeded PRNG (mulberry32) so the sample data is reproducible. */
function createRng(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let t = state
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

function hashString(value: string): number {
  let hash = 2166136261
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 16777619)
  }
  return hash >>> 0
}

const roundPesos = (amount: number) => Math.round(amount / 100) * 100
const pad = (value: number) => String(value).padStart(2, '0')

function isoDate(date: Date): string {
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`
}

/** Typical daily takings of a neighbourhood butcher shop, in ARS. */
const DAILY_BASE = { counter: 420_000, delivery: 150_000, web: 95_000 }
/** Average ticket per channel, used to derive order counts from takings. */
const AVERAGE_TICKET = { counter: 18_500, delivery: 24_000, web: 31_000 }
/** Relative foot traffic for the opening hours 08:00 to 20:00. */
const HOUR_WEIGHTS = [2, 4, 5, 4, 3, 3, 4, 6, 7, 5, 3, 2, 1]
const FIRST_HOUR = 8

const PRODUCT_CATALOG: ReadonlyArray<{
  id: string
  name: string
  unit: 'kg' | 'u'
  price: number
  dailyQuantity: number
}> = [
  { id: 'p-asado', name: 'Asado', unit: 'kg', price: 11_800, dailyQuantity: 42 },
  { id: 'p-vacio', name: 'Vacío', unit: 'kg', price: 12_900, dailyQuantity: 26 },
  { id: 'p-matambre', name: 'Matambre', unit: 'kg', price: 10_400, dailyQuantity: 19 },
  { id: 'p-milanesas', name: 'Milanesas de nalga', unit: 'kg', price: 11_200, dailyQuantity: 31 },
  { id: 'p-chorizo', name: 'Chorizo parrillero', unit: 'kg', price: 7_900, dailyQuantity: 28 },
  { id: 'p-pollo', name: 'Pollo entero', unit: 'u', price: 6_800, dailyQuantity: 22 },
  { id: 'p-picada', name: 'Carne picada especial', unit: 'kg', price: 8_600, dailyQuantity: 34 },
  { id: 'p-costilla', name: 'Costilla', unit: 'kg', price: 9_800, dailyQuantity: 15 },
]

const STOCK_RISK: StockRiskItem[] = [
  { id: 'p-vacio', name: 'Vacío', unit: 'kg', currentStock: 6, minimumStock: 20 },
  { id: 'p-matambre', name: 'Matambre', unit: 'kg', currentStock: 4, minimumStock: 12 },
  { id: 'p-chorizo', name: 'Chorizo parrillero', unit: 'kg', currentStock: 11, minimumStock: 25 },
  { id: 'p-morcilla', name: 'Morcilla', unit: 'u', currentStock: 14, minimumStock: 30 },
  { id: 'p-pollo', name: 'Pollo entero', unit: 'u', currentStock: 9, minimumStock: 15 },
]

const CURRENT_ACCOUNTS: CurrentAccount[] = [
  { id: 'c-club', customer: 'Club Social Barrio Norte', balance: 275_800, daysOverdue: 45 },
  { id: 'c-estancia', customer: 'Restaurante La Estancia', balance: 485_000, daysOverdue: 32 },
  { id: 'c-julio', customer: 'Parrilla Don Julio', balance: 312_500, daysOverdue: 18 },
  { id: 'c-alamos', customer: 'Rotisería Los Álamos', balance: 198_000, daysOverdue: 9 },
  { id: 'c-marta', customer: 'Marta Gutiérrez', balance: 64_300, daysOverdue: 0 },
]

const MONEY_ON_HAND = 1_284_300
const PERIOD_DAYS: Record<DashboardPeriod, number> = { today: 1, '7d': 7, '30d': 30 }
const CHANNELS = ['counter', 'delivery', 'web'] as const

function buildSeries(period: DashboardPeriod, now: Date, rng: () => number) {
  const orderCounts = { counter: 0, delivery: 0, web: 0 }
  const points: SalesPoint[] = []

  const addBucket = (bucket: string, share: number, dayFactor: number) => {
    const amount = (base: number) => roundPesos(base * share * dayFactor * (0.8 + rng() * 0.4))
    const point: SalesPoint = {
      bucket,
      counter: amount(DAILY_BASE.counter),
      delivery: amount(DAILY_BASE.delivery),
      web: amount(DAILY_BASE.web),
    }
    for (const channel of CHANNELS) {
      orderCounts[channel] += Math.max(1, Math.round((point[channel] / AVERAGE_TICKET[channel]) * (0.9 + rng() * 0.2)))
    }
    points.push(point)
  }

  if (period === 'today') {
    const totalWeight = HOUR_WEIGHTS.reduce((acc, weight) => acc + weight, 0)
    HOUR_WEIGHTS.forEach((weight, index) => {
      addBucket(`${isoDate(now)}T${pad(FIRST_HOUR + index)}:00`, weight / totalWeight, 1)
    })
    return { granularity: 'hour' as const, points, orderCounts }
  }

  for (let offset = PERIOD_DAYS[period] - 1; offset >= 0; offset -= 1) {
    const day = new Date(now.getTime() - offset * 86_400_000)
    const dayOfWeek = day.getUTCDay()
    const dayFactor = dayOfWeek === 5 || dayOfWeek === 6 ? 1.35 : dayOfWeek === 0 ? 0.6 : 1
    addBucket(isoDate(day), 1, dayFactor)
  }
  return { granularity: 'day' as const, points, orderCounts }
}

function buildTopProducts(period: DashboardPeriod, rng: () => number): TopProduct[] {
  const days = PERIOD_DAYS[period]
  return PRODUCT_CATALOG.map((product) => {
    const quantity = Math.round(product.dailyQuantity * days * (0.85 + rng() * 0.3))
    return { id: product.id, name: product.name, unit: product.unit, quantity, revenue: quantity * product.price }
  })
    .sort((a, b) => b.revenue - a.revenue)
    .slice(0, 5)
}

function sum(values: number[]): number {
  return values.reduce((acc, value) => acc + value, 0)
}

/**
 * `now` is injectable so tests pin the series dates; the figures themselves
 * are seeded by period and scope, never by `Math.random`.
 */
export function createMockDashboardSource(options: { now?: () => Date } = {}): DashboardSource {
  const now = options.now ?? (() => new Date())
  return {
    async load({ period, scope }: DashboardQuery): Promise<DashboardData> {
      const rng = createRng(hashString(`${period}|${scope.organizationId ?? ''}|${scope.branchId ?? ''}`))
      const { granularity, points, orderCounts } = buildSeries(period, now(), rng)

      const counterSales = sum(points.map((point) => point.counter))
      const deliverySales = sum(points.map((point) => point.delivery))
      const webTotal = sum(points.map((point) => point.web))
      const posTotal = counterSales + deliverySales

      return {
        period,
        kpis: {
          posOrders: { count: orderCounts.counter + orderCounts.delivery, total: posTotal },
          webOrders: { count: orderCounts.web, total: webTotal },
          grandTotal: posTotal + webTotal,
          moneyOnHand: MONEY_ON_HAND,
          receivables: {
            total: sum(CURRENT_ACCOUNTS.map((account) => account.balance)),
            accounts: CURRENT_ACCOUNTS.length,
          },
          deliverySales,
          counterSales,
        },
        series: { granularity, points },
        topProducts: buildTopProducts(period, rng),
        stockRisk: [...STOCK_RISK].sort((a, b) => a.currentStock / a.minimumStock - b.currentStock / b.minimumStock),
        currentAccounts: CURRENT_ACCOUNTS.map((account) => ({ ...account })),
      }
    },
  }
}

export const mockDashboardSource: DashboardSource = createMockDashboardSource()
