import { useTranslation } from 'react-i18next'
import { formatArs, formatQuantity } from '@/dashboard/format'
import type { TopProduct } from '@/dashboard/types'
import { DashboardCard } from './DashboardCard'

export function TopProducts({ products, className }: { products: TopProduct[]; className?: string }) {
  const { t } = useTranslation('dashboard')
  const maxRevenue = Math.max(0, ...products.map((product) => product.revenue))

  return (
    <DashboardCard title={t('topProducts.title')} className={className}>
      {products.length === 0 ? (
        <p data-empty="top-products" className="text-sm text-muted-foreground">
          {t('topProducts.empty')}
        </p>
      ) : (
        <ol className="flex flex-col gap-3">
          {products.map((product) => (
            <li key={product.id} className="flex flex-col gap-1">
              <div className="flex items-baseline justify-between gap-2">
                <span className="min-w-0 truncate text-sm font-medium text-foreground">{product.name}</span>
                <span className="shrink-0 text-sm text-foreground">{formatArs(product.revenue)}</span>
              </div>
              <div className="h-1.5 w-full rounded-full bg-muted" aria-hidden="true">
                <div
                  className="h-full rounded-full bg-chart-1"
                  style={{ width: `${maxRevenue === 0 ? 0 : (product.revenue / maxRevenue) * 100}%` }}
                />
              </div>
              <span className="text-xs text-muted-foreground">{formatQuantity(product.quantity, product.unit)}</span>
            </li>
          ))}
        </ol>
      )}
    </DashboardCard>
  )
}
