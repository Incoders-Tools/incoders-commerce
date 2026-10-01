import { useTranslation } from 'react-i18next'
import { formatArs } from '@/dashboard/format'
import { DashboardCard } from './DashboardCard'
import { channelMeta } from './channels'

/** Delivery (reparto) vs counter (mostrador) POS sales as a proportional bar. */
export function ChannelSplit({
  deliverySales,
  counterSales,
  className,
}: {
  deliverySales: number
  counterSales: number
  className?: string
}) {
  const { t } = useTranslation('dashboard')
  const total = deliverySales + counterSales
  const deliveryShare = total === 0 ? 0 : (deliverySales / total) * 100
  const rows = [
    { key: 'delivery', value: deliverySales, share: deliveryShare },
    { key: 'counter', value: counterSales, share: total === 0 ? 0 : 100 - deliveryShare },
  ] as const

  return (
    <DashboardCard title={t('channels.split')} className={className}>
      <div className="flex h-3 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
        {rows.map((row) => (
          <div key={row.key} style={{ width: `${row.share}%`, backgroundColor: channelMeta[row.key].color }} />
        ))}
      </div>
      <dl className="flex flex-col gap-3">
        {rows.map((row) => (
          <div key={row.key} className="flex items-center justify-between gap-2">
            <dt className="flex min-w-0 items-center gap-2 text-sm text-muted-foreground">
              <span
                aria-hidden="true"
                className="size-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: channelMeta[row.key].color }}
              />
              <span className="truncate">{t(channelMeta[row.key].labelKey)}</span>
            </dt>
            <dd className="shrink-0 text-sm font-medium text-foreground">
              {formatArs(row.value)}
              <span className="ml-2 text-xs font-normal text-muted-foreground">{Math.round(row.share)}%</span>
            </dd>
          </div>
        ))}
      </dl>
    </DashboardCard>
  )
}
