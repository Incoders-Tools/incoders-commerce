import { useTranslation } from 'react-i18next'
import { formatMoney } from '@/dashboard/format'
import type { FloorViolation } from '@/api/types'

/**
 * The refusal of a change that would put products below their floor list (409
 * `price-below-floor`; the server wrote nothing). Names every product with the price it
 * would get and the price it may not go under.
 */
export function FloorViolationsTable({ violations }: { violations: FloorViolation[] }) {
  const { t } = useTranslation('priceLists')
  const floors = [...new Set(violations.map((violation) => violation.floorPriceListName))].join(', ')
  return (
    <div role="alert" className="flex flex-col gap-3 rounded-md border border-destructive/40 bg-destructive/10 p-4 text-sm">
      <div>
        <p className="font-medium text-destructive">{t('violations.title', { floor: floors })}</p>
        <p className="text-muted-foreground">{t('violations.hint')}</p>
      </div>
      <div className="w-full overflow-x-auto rounded-md border border-border bg-card">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-border text-xs text-muted-foreground uppercase">
            <tr>
              <th scope="col" className="px-3 py-2 font-medium">{t('violations.columns.list')}</th>
              <th scope="col" className="px-3 py-2 font-medium">{t('violations.columns.product')}</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">{t('violations.columns.price')}</th>
              <th scope="col" className="px-3 py-2 text-right font-medium">{t('violations.columns.floor')}</th>
            </tr>
          </thead>
          <tbody>
            {violations.map((violation) => (
              <tr key={`${violation.priceListId}:${violation.presentationId}`} className="border-b border-border last:border-0">
                <td className="px-3 py-2">{violation.priceListName}</td>
                <td className="px-3 py-2">{`${violation.productName} · ${violation.presentationName}`}</td>
                <td className="px-3 py-2 text-right tabular-nums">{formatMoney(violation.price)}</td>
                <td className="px-3 py-2 text-right tabular-nums">{formatMoney(violation.floorPrice)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
