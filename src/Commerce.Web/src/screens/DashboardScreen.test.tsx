import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { DashboardSource } from '@/dashboard/port'
import { formatArs } from '@/dashboard/format'
import type { DashboardData, DashboardPeriod } from '@/dashboard/types'
import { DashboardScreen } from './DashboardScreen'

// Intl emits a no-break space after the currency symbol; Testing Library matches normalized text.
const ars = (amount: number) => formatArs(amount).replace(/\s/g, ' ')

function buildData(period: DashboardPeriod = '7d', overrides: Partial<DashboardData> = {}): DashboardData {
  return {
    period,
    kpis: {
      posOrders: { count: 120, total: 2_400_000 },
      webOrders: { count: 35, total: 900_000 },
      grandTotal: 3_300_000,
      moneyOnHand: 1_284_300,
      receivables: { total: 1_335_600, accounts: 5 },
      deliverySales: 600_000,
      counterSales: 1_800_000,
    },
    series: {
      granularity: 'day',
      points: [
        { bucket: '2026-09-30', counter: 900_000, delivery: 300_000, web: 400_000 },
        { bucket: '2026-10-01', counter: 900_000, delivery: 300_000, web: 500_000 },
      ],
    },
    topProducts: [
      { id: 'p1', name: 'Asado', unit: 'kg', quantity: 42.5, revenue: 501_500 },
      { id: 'p2', name: 'Vacío', unit: 'kg', quantity: 20, revenue: 258_000 },
    ],
    stockRisk: [{ id: 'p3', name: 'Matambre', unit: 'kg', currentStock: 4, minimumStock: 12 }],
    currentAccounts: [
      { id: 'c1', customer: 'Parrilla Don Julio', balance: 312_500, daysOverdue: 18 },
      { id: 'c2', customer: 'Marta Gutiérrez', balance: 64_300, daysOverdue: 0 },
    ],
    ...overrides,
  }
}

function fakeSource(factory: (period: DashboardPeriod) => DashboardData = (period) => buildData(period)) {
  const load = vi.fn<DashboardSource['load']>(async ({ period }) => factory(period))
  return { source: { load } satisfies DashboardSource, load }
}

describe('DashboardScreen', () => {
  it('labels every business metric once the data loads', async () => {
    const { source } = fakeSource()
    render(<DashboardScreen source={source} />)

    expect(await screen.findByRole('heading', { name: 'Tablero' })).toBeInTheDocument()
    for (const name of [
      'Pedidos POS',
      'Pedidos web',
      'Total general',
      'Dinero en caja',
      'Por cobrar',
      'Reparto vs. mostrador',
      'Ventas por canal',
      'Productos más vendidos',
      'Stock en riesgo',
      'Cuentas corrientes',
    ]) {
      expect(await screen.findByText(name)).toBeInTheDocument()
    }
  })

  it('shows the figures formatted as Argentine pesos', async () => {
    const { source } = fakeSource()
    render(<DashboardScreen source={source} />)

    const grandTotal = (await screen.findByText('Total general')).closest('[data-kpi]') as HTMLElement
    expect(within(grandTotal).getByText(ars(3_300_000))).toBeInTheDocument()
    const receivables = screen.getByText('Por cobrar').closest('[data-kpi]') as HTMLElement
    expect(within(receivables).getByText(ars(1_335_600))).toBeInTheDocument()
    expect(within(receivables).getByText('5 cuentas')).toBeInTheDocument()
    const pos = screen.getByText('Pedidos POS').closest('[data-kpi]') as HTMLElement
    expect(within(pos).getByText('120 pedidos')).toBeInTheDocument()
    expect(formatArs(1_284_300).replace(/\s/g, ' ')).toBe('$ 1.284.300')
  })

  it('lists top products, stock risk and current accounts', async () => {
    const { source } = fakeSource()
    render(<DashboardScreen source={source} />)

    expect(await screen.findByText('Asado')).toBeInTheDocument()
    expect(screen.getByText('Vacío')).toBeInTheDocument()
    expect(screen.getByText('Matambre')).toBeInTheDocument()
    expect(screen.getByText(/Mínimo 12/)).toBeInTheDocument()
    const table = screen.getByRole('table', { name: 'Cuentas corrientes' })
    expect(within(table).getByText('Parrilla Don Julio')).toBeInTheDocument()
    expect(within(table).getByText('18 días')).toBeInTheDocument()
    expect(within(table).getByText('Al día')).toBeInTheDocument()
  })

  it('shows a visible sample-data notice', async () => {
    const { source } = fakeSource()
    render(<DashboardScreen source={source} />)

    expect(await screen.findByText(/datos de muestra/i)).toBeVisible()
  })

  it('loads the last 7 days first and refetches when the period changes', async () => {
    const user = userEvent.setup()
    const { source, load } = fakeSource()
    render(<DashboardScreen source={source} />)

    await screen.findByText('Asado')
    expect(load).toHaveBeenCalledTimes(1)
    expect(load).toHaveBeenLastCalledWith({ scope: { organizationId: null, branchId: null }, period: '7d' })
    const group = screen.getByRole('group', { name: 'Período' })
    expect(within(group).getByRole('button', { name: '7 días' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(within(group).getByRole('button', { name: '30 días' }))

    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(2))
    expect(load).toHaveBeenLastCalledWith({ scope: { organizationId: null, branchId: null }, period: '30d' })
    expect(within(group).getByRole('button', { name: '30 días' })).toHaveAttribute('aria-pressed', 'true')
    expect(within(group).getByRole('button', { name: 'Hoy' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('shows a loading state until the source answers', async () => {
    const source: DashboardSource = { load: () => new Promise(() => {}) }
    render(<DashboardScreen source={source} />)

    expect(await screen.findByRole('status')).toHaveTextContent('Cargando tablero')
    expect(screen.queryByText('Pedidos POS')).not.toBeInTheDocument()
  })

  it('shows an error with a retry that reloads', async () => {
    const user = userEvent.setup()
    const load = vi
      .fn<DashboardSource['load']>()
      .mockRejectedValueOnce(new Error('boom'))
      .mockResolvedValueOnce(buildData())
    render(<DashboardScreen source={{ load }} />)

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo cargar el tablero')

    await user.click(screen.getByRole('button', { name: 'Reintentar' }))

    expect(await screen.findByText('Asado')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(load).toHaveBeenCalledTimes(2)
  })

  it('shows empty states instead of blank widgets when there is nothing to show', async () => {
    const { source } = fakeSource(() =>
      buildData('7d', {
        series: { granularity: 'day', points: [] },
        topProducts: [],
        stockRisk: [],
        currentAccounts: [],
      }),
    )
    render(<DashboardScreen source={source} />)

    expect(await screen.findByText('Sin ventas en este período.', { selector: '[data-empty="trend"]' })).toBeInTheDocument()
    expect(screen.getByText('Sin ventas en este período.', { selector: '[data-empty="top-products"]' })).toBeInTheDocument()
    expect(screen.getByText('Ningún producto bajo el mínimo.')).toBeInTheDocument()
    expect(screen.getByText('Sin cuentas pendientes.')).toBeInTheDocument()
  })

  it('offers the sales trend as an accessible table', async () => {
    const user = userEvent.setup()
    const { source } = fakeSource()
    render(<DashboardScreen source={source} />)

    await screen.findByText('Ventas por canal')
    expect(screen.queryByRole('table', { name: 'Ventas por canal' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Ver como tabla' }))

    const table = screen.getByRole('table', { name: 'Ventas por canal' })
    expect(within(table).getByRole('columnheader', { name: 'Mostrador' })).toBeInTheDocument()
    expect(within(table).getByRole('columnheader', { name: 'Reparto' })).toBeInTheDocument()
    expect(within(table).getByRole('columnheader', { name: 'Web' })).toBeInTheDocument()
    expect(within(table).getByText(ars(500_000))).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Ver gráfico' }))
    expect(screen.queryByRole('table', { name: 'Ventas por canal' })).not.toBeInTheDocument()
  })
})
