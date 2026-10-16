import { useTranslation } from 'react-i18next'
import { formatArs } from '@/dashboard/format'
import type { DashboardKpis } from '@/dashboard/types'
import { cn } from '@/lib/utils'

function KpiTile({
  label,
  value,
  detail,
  hero = false,
  className,
}: {
  label: string
  value: string
  detail?: string
  hero?: boolean
  className?: string
}) {
  return (
    <div data-kpi className={cn('min-w-0 rounded-lg border border-border bg-card p-4 shadow-sm md:p-5', className)}>
      <p className="truncate text-sm text-muted-foreground">{label}</p>
      <p className={cn('mt-1 truncate font-semibold tracking-tight text-foreground', hero ? 'text-4xl' : 'text-2xl')}>
        {value}
      </p>
      {detail && <p className="mt-1 truncate text-xs text-muted-foreground">{detail}</p>}
    </div>
  )
}

/** Headline figures. Presentational: props only. */
export function KpiTiles({ kpis }: { kpis: DashboardKpis }) {
  const { t } = useTranslation('dashboard')
  return (
    <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-12">
      <KpiTile
        hero
        className="sm:col-span-2 xl:col-span-4"
        label={t('kpis.grandTotal')}
        value={formatArs(kpis.grandTotal)}
      />
      <KpiTile
        className="xl:col-span-2"
        label={t('kpis.posOrders')}
        value={formatArs(kpis.posOrders.total)}
        detail={t('kpis.orderCount', { count: kpis.posOrders.count })}
      />
      <KpiTile
        className="xl:col-span-2"
        label={t('kpis.webOrders')}
        value={formatArs(kpis.webOrders.total)}
        detail={t('kpis.orderCount', { count: kpis.webOrders.count })}
      />
      <KpiTile className="xl:col-span-2" label={t('kpis.moneyOnHand')} value={formatArs(kpis.moneyOnHand)} />
      <KpiTile
        className="xl:col-span-2"
        label={t('kpis.receivables')}
        value={formatArs(kpis.receivables.total)}
        detail={t('kpis.accountCount', { count: kpis.receivables.accounts })}
      />
    </div>
  )
}
