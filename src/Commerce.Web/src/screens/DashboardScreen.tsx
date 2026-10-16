import { useEffect, useState } from 'react'
import { Info } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { ChannelSplit } from '@/components/dashboard/ChannelSplit'
import { CurrentAccountsTable } from '@/components/dashboard/CurrentAccountsTable'
import { KpiTiles } from '@/components/dashboard/KpiTiles'
import { PeriodSelector } from '@/components/dashboard/PeriodSelector'
import { SalesTrendChart } from '@/components/dashboard/SalesTrendChart'
import { StockRiskList } from '@/components/dashboard/StockRiskList'
import { TopProducts } from '@/components/dashboard/TopProducts'
import { PageHeader } from '@/components/data/PageHeader'
import { Button } from '@/components/ui/button'
import { SAMPLE_DATA, dashboardSource } from '@/dashboard/dashboardSource'
import type { DashboardSource } from '@/dashboard/port'
import type { DashboardData, DashboardPeriod } from '@/dashboard/types'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

type LoadState = { status: 'loading' | 'error'; data: null } | { status: 'ready'; data: DashboardData }

/**
 * Business dashboard (container). Fetches through the `DashboardSource` port
 * and hands props to presentational widgets in `components/dashboard/*`.
 * `AppLayout` remounts routed screens on organization/branch switches, so the
 * scope read here is always current for the mounted screen.
 */
export function DashboardScreen({ source = dashboardSource }: { source?: DashboardSource }) {
  const { t } = useTranslation('dashboard')
  const organizationId = useOptionalOrganizationContext()?.selectedOrganization?.id ?? null
  const branchId = useOptionalBranchContext()?.selectedBranch?.id ?? null
  const [period, setPeriod] = useState<DashboardPeriod>('7d')
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<LoadState>({ status: 'loading', data: null })

  useEffect(() => {
    let cancelled = false
    source.load({ scope: { organizationId, branchId }, period }).then(
      (data) => {
        if (!cancelled) setState({ status: 'ready', data })
      },
      () => {
        if (!cancelled) setState({ status: 'error', data: null })
      },
    )
    return () => {
      cancelled = true
    }
  }, [source, organizationId, branchId, period, attempt])

  const changePeriod = (next: DashboardPeriod) => {
    if (next === period) return
    setState((previous) => (previous.status === 'ready' ? previous : { status: 'loading', data: null }))
    setPeriod(next)
  }

  const retry = () => {
    setState({ status: 'loading', data: null })
    setAttempt((previous) => previous + 1)
  }

  const data = state.data

  return (
    <div className="flex flex-col gap-4 md:gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={<PeriodSelector value={period} onChange={changePeriod} />}
      />

      {SAMPLE_DATA && (
        <p className="flex items-start gap-2 rounded-lg border border-border bg-muted px-3 py-2 text-sm text-muted-foreground">
          <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
          {t('sampleNotice')}
        </p>
      )}

      {state.status === 'loading' && (
        <p role="status" className="text-sm text-muted-foreground">
          {t('states.loading')}
        </p>
      )}

      {state.status === 'error' && (
        <div role="alert" className="flex flex-wrap items-center gap-3 rounded-lg border border-destructive p-4 text-sm">
          <span className="text-foreground">{t('states.error')}</span>
          <Button type="button" variant="outline" size="sm" onClick={retry}>
            {t('states.retry')}
          </Button>
        </div>
      )}

      {data && (
        <>
          <KpiTiles kpis={data.kpis} />
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 md:gap-6 xl:grid-cols-12">
            <SalesTrendChart series={data.series} className="md:col-span-2 xl:col-span-8" />
            <ChannelSplit
              deliverySales={data.kpis.deliverySales}
              counterSales={data.kpis.counterSales}
              className="xl:col-span-4"
            />
            <TopProducts products={data.topProducts} className="xl:col-span-4" />
            <StockRiskList items={data.stockRisk} className="xl:col-span-4" />
            <CurrentAccountsTable accounts={data.currentAccounts} className="xl:col-span-4" />
          </div>
        </>
      )}
    </div>
  )
}
