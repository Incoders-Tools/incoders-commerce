import { describe, expect, it } from 'vitest'
import { createMockDashboardSource } from './mockDashboardSource'
import { dashboardSource } from './dashboardSource'
import type { DashboardPeriod } from './types'

const fixedNow = () => new Date('2026-10-01T15:30:00Z')
const scope = { organizationId: 'org-vaca-verde', branchId: 'branch-centro' }

function load(period: DashboardPeriod, overrides: Partial<typeof scope> = {}) {
  return createMockDashboardSource({ now: fixedNow }).load({ scope: { ...scope, ...overrides }, period })
}

describe('mock dashboard source', () => {
  it('is the source the app uses until real data is wired', () => {
    expect(typeof dashboardSource.load).toBe('function')
  })

  it('is deterministic for the same period and scope', async () => {
    expect(await load('7d')).toEqual(await load('7d'))
  })

  it('differs between periods', async () => {
    const [today, month] = await Promise.all([load('today'), load('30d')])
    expect(month.kpis.grandTotal).toBeGreaterThan(today.kpis.grandTotal)
  })

  it('buckets the series hourly for today and daily for 7 and 30 days', async () => {
    const today = await load('today')
    const week = await load('7d')
    const month = await load('30d')

    expect(today.series.granularity).toBe('hour')
    expect(week.series.granularity).toBe('day')
    expect(week.series.points).toHaveLength(7)
    expect(month.series.points).toHaveLength(30)
    expect(week.series.points.at(-1)?.bucket).toBe('2026-10-01')
  })

  it('keeps the KPIs consistent with the series and with each other', async () => {
    for (const period of ['today', '7d', '30d'] as const) {
      const { kpis, series } = await load(period)
      const sum = (key: 'counter' | 'delivery' | 'web') => series.points.reduce((acc, point) => acc + point[key], 0)

      expect(kpis.counterSales).toBe(sum('counter'))
      expect(kpis.deliverySales).toBe(sum('delivery'))
      expect(kpis.webOrders.total).toBe(sum('web'))
      expect(kpis.posOrders.total).toBe(kpis.counterSales + kpis.deliverySales)
      expect(kpis.grandTotal).toBe(kpis.posOrders.total + kpis.webOrders.total)
      expect(kpis.posOrders.count).toBeGreaterThan(0)
      expect(kpis.webOrders.count).toBeGreaterThan(0)
    }
  })

  it('derives receivables from the current accounts', async () => {
    const { kpis, currentAccounts } = await load('7d')

    expect(kpis.receivables.accounts).toBe(currentAccounts.length)
    expect(kpis.receivables.total).toBe(currentAccounts.reduce((acc, account) => acc + account.balance, 0))
    expect(kpis.moneyOnHand).toBeGreaterThan(0)
  })

  it('returns butcher-shop products ordered by revenue, and stock risk below the minimum', async () => {
    const { topProducts, stockRisk } = await load('30d')

    expect(topProducts.length).toBeGreaterThan(0)
    expect(topProducts.length).toBeLessThanOrEqual(5)
    expect(topProducts.map((product) => product.name)).toContain('Asado')
    const revenues = topProducts.map((product) => product.revenue)
    expect(revenues).toEqual([...revenues].sort((a, b) => b - a))
    expect(stockRisk.length).toBeGreaterThan(0)
    for (const item of stockRisk) expect(item.currentStock).toBeLessThan(item.minimumStock)
  })

  it('does not depend on Math.random', async () => {
    const original = Math.random
    Math.random = () => {
      throw new Error('Math.random must not be used')
    }
    try {
      await expect(load('7d')).resolves.toBeDefined()
    } finally {
      Math.random = original
    }
  })
})
