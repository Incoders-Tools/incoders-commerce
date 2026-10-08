import { TriangleAlert } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { formatQuantity } from '@/dashboard/format'
import type { StockRiskItem } from '@/dashboard/types'
import { DashboardCard } from './DashboardCard'

/** Products under their minimum stock. Status is carried by icon + text, never colour alone. */
export function StockRiskList({ items, className }: { items: StockRiskItem[]; className?: string }) {
  const { t } = useTranslation('dashboard')

  return (
    <DashboardCard title={t('stockRisk.title')} className={className}>
      {items.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('stockRisk.empty')}</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {items.map((item) => (
            <li key={item.id} className="flex items-center gap-3">
              <TriangleAlert aria-hidden="true" className="size-4 shrink-0 text-destructive" />
              <span className="min-w-0 flex-1 truncate text-sm font-medium text-foreground">{item.name}</span>
              <span className="shrink-0 text-right text-xs text-muted-foreground">
                <span className="block text-sm font-medium text-foreground">
                  {formatQuantity(item.currentStock, item.unit)}
                </span>
                {t('stockRisk.minimum', { value: formatQuantity(item.minimumStock, item.unit) })}
              </span>
            </li>
          ))}
        </ul>
      )}
    </DashboardCard>
  )
}
