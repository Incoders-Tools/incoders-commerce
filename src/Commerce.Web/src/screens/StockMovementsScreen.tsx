import { useEffect, useState } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { FormPage } from '@/components/layout/FormPage'
import { listStockMovements } from '@/api/stock'
import { ApiError } from '@/api/client'
import type { StockHistoryPage, StockMovement, StockQuantityBehavior } from '@/api/types'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { useNumberFormat } from '@/organization/NumberFormatContext'
import { cn } from '@/lib/utils'

const PAGE_SIZE = 25
const dateTimeFormatter = new Intl.DateTimeFormat('es-AR', { dateStyle: 'short', timeStyle: 'short' })

interface NavigationState {
  label?: string
  behavior?: StockQuantityBehavior
}


/**
 * Append-only movement history of one presentation, newest first, with the
 * running balance after each movement. Movements that came from a goods
 * reception link back to it.
 */
export function StockMovementsScreen() {
  const { t } = useTranslation('stock')
  const numberFormat = useNumberFormat()

  /** `+120 kg`, `-2,5 kg`; without the unit when the screen was opened without the presentation's behavior. */
  const signedQuantity = (quantity: number, behavior: StockQuantityBehavior | undefined): string => {
    const unit = behavior ? (behavior === 'FixedQuantity' ? ' u' : ' kg') : ''
    return `${numberFormat.formatSigned(quantity)}${unit}`
  }
  const quantityText = (quantity: number, behavior: StockQuantityBehavior | undefined) =>
    behavior ? numberFormat.formatStock(quantity, behavior) : numberFormat.formatNumber(quantity)
  const { presentationId = '' } = useParams()
  const navigate = useNavigate()
  const state = (useLocation().state as NavigationState | null) ?? {}
  const missingBranch = useMissingBranch()
  const [pageNumber, setPageNumber] = useState(1)
  const [history, setHistory] = useState<StockHistoryPage | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    if (missingBranch) return
    let current = true
    listStockMovements(presentationId, pageNumber, PAGE_SIZE).then(
      (result) => {
        if (!current) return
        setHistory(result)
        setLoadError(null)
      },
      (err: unknown) => {
        if (current) setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
      },
    )
    return () => {
      current = false
    }
  }, [presentationId, pageNumber, missingBranch, t])

  const pages = history ? Math.max(1, Math.ceil(history.total / history.pageSize)) : 1
  const noValue = <span className="text-muted-foreground">{t('movements.columns.noValue')}</span>

  const sourceCell = (movement: StockMovement) => {
    if (!movement.sourceNumber) return noValue
    return movement.sourceType?.startsWith('PurchaseReception') && movement.sourceId ? (
      <Link className="font-medium text-primary underline" to={`/app/receptions/${movement.sourceId}`}>
        {movement.sourceNumber}
      </Link>
    ) : (
      movement.sourceNumber
    )
  }

  return (
    <FormPage
      title={t('movements.title')}
      description={state.label}
      onBack={() => navigate('/app/stock')}
      backLabel={t('movements.backLabel')}
    >
      <div className="flex flex-col gap-6">
        {missingBranch && (
          <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
            {t('branchRequired')}
          </p>
        )}
        {loadError && (
          <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {loadError}
          </p>
        )}
        {history && (
          <>
            <p className="text-sm font-semibold">
              {t('movements.onHand', { value: quantityText(history.onHand, state.behavior) })}
            </p>
            {history.items.length === 0 ? (
              <p className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
                {t('movements.empty')}
              </p>
            ) : (
              <div className="overflow-x-auto rounded-lg border border-border">
                <table className="w-full text-sm">
                  <thead className="bg-muted text-left text-xs uppercase tracking-wide text-muted-foreground">
                    <tr>
                      <th className="px-3 py-2">{t('movements.columns.date')}</th>
                      <th className="px-3 py-2">{t('movements.columns.kind')}</th>
                      <th className="px-3 py-2 text-right">{t('movements.columns.quantity')}</th>
                      <th className="px-3 py-2 text-right">{t('movements.columns.balance')}</th>
                      <th className="px-3 py-2">{t('movements.columns.source')}</th>
                      <th className="px-3 py-2">{t('movements.columns.reason')}</th>
                      <th className="px-3 py-2">{t('movements.columns.lot')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {history.items.map((movement) => (
                      <tr key={movement.id} className="border-t border-border">
                        <td className="px-3 py-2">{dateTimeFormatter.format(new Date(movement.occurredAtUtc))}</td>
                        <td className="px-3 py-2">{t(`movements.kinds.${movement.kind}`)}</td>
                        <td
                          className={cn(
                            'px-3 py-2 text-right tabular-nums',
                            movement.quantity < 0 && 'text-destructive',
                          )}
                        >
                          {signedQuantity(movement.quantity, state.behavior)}
                        </td>
                        <td className="px-3 py-2 text-right tabular-nums">
                          {quantityText(movement.balanceAfter, state.behavior)}
                        </td>
                        <td className="px-3 py-2">{sourceCell(movement)}</td>
                        <td className="px-3 py-2">{movement.reason ?? noValue}</td>
                        <td className="px-3 py-2">{movement.lotCode ?? noValue}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {pages > 1 && (
              <div className="flex items-center justify-between gap-3">
                <Button variant="outline" size="sm" disabled={pageNumber <= 1} onClick={() => setPageNumber((n) => n - 1)}>
                  {t('movements.pager.previous')}
                </Button>
                <span className="text-sm text-muted-foreground">
                  {t('movements.pager.page', { page: pageNumber, pages })}
                </span>
                <Button variant="outline" size="sm" disabled={pageNumber >= pages} onClick={() => setPageNumber((n) => n + 1)}>
                  {t('movements.pager.next')}
                </Button>
              </div>
            )}
          </>
        )}
      </div>
    </FormPage>
  )
}
